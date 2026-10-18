using System.Security.Cryptography;
using Saldo360.Domain.CreditCards;

namespace Saldo360.Application.CreditCards;

public interface IFinancialDocumentStorage
{
    Task<string> SaveAsync(Guid familyId, Guid documentId, Stream content, CancellationToken cancellationToken);
    Task<Stream?> OpenReadAsync(string reference, CancellationToken cancellationToken);
    Task DeleteAsync(string reference, CancellationToken cancellationToken);
}

public interface IFinancialDocumentInboxRepository
{
    Task<bool> CanAccessFamilyAsync(Guid familyId, Guid userId, CancellationToken cancellationToken);
    Task<bool> FamilyExistsAsync(Guid familyId, CancellationToken cancellationToken);
    Task<FinancialDocumentInboxItem?> FindByHashAsync(Guid familyId, string hash, CancellationToken cancellationToken);
    Task<FinancialDocumentInboxItem?> FindByExternalIdAsync(Guid familyId, FinancialDocumentSource source, string externalId, CancellationToken cancellationToken);
    Task<FinancialDocumentInboxItem?> GetAsync(Guid familyId, Guid id, Guid userId, CancellationToken cancellationToken);
    Task<IReadOnlyList<FinancialDocumentInboxItem>> ListAsync(Guid familyId, Guid userId, CancellationToken cancellationToken);
    Task<bool> TryAddAsync(FinancialDocumentInboxItem item, CancellationToken cancellationToken);
    Task SaveAsync(CancellationToken cancellationToken);
}

public sealed record FinancialDocumentInboxDto(Guid Id, string Source, string? ExternalSourceId, string OriginalFileName,
    string ContentHash, string ContentType, string ReceivedAt, string Status, string? Parser, string? Competence,
    Guid? CreditCardId, string? FailureCode, string UpdatedAt, string? DocumentType = null, string? ClassificationConfidence = null,
    string? ClassificationSource = null, string? ClassificationFailureCode = null, string? ClassifiedAt = null);
public sealed record FinancialDocumentInboxReceiveResult(string Result, FinancialDocumentInboxDto? Item, IDictionary<string, string[]>? Errors = null);

public sealed class FinancialDocumentInboxService(
    IFinancialDocumentInboxRepository repository,
    IFinancialDocumentStorage storage,
    ICreditCardPdfTextExtractor extractor,
    ICreditCardPdfPreviewService previewService,
    CreditCardStatementResolver resolver,
    FinancialDocumentClassificationOptions classificationOptions,
    IFinancialDocumentClassifier classifier,
    IFinancialDocumentAnalysisRouter analysisRouter,
    TimeProvider clock)
{
    public async Task<FinancialDocumentInboxReceiveResult> ReceiveAsync(Guid familyId, Guid userId, Stream content, long length,
        string fileName, string contentType, FinancialDocumentSource source, string? externalSourceId, Guid? selectedCardId,
        string? password, CancellationToken cancellationToken) =>
        await ReceiveCoreAsync(familyId, userId, true, content, length, fileName, contentType, source, externalSourceId, selectedCardId, password, null, cancellationToken);

    public async Task<FinancialDocumentInboxReceiveResult> ReceiveAutomationAsync(Guid familyId, Stream content, long length,
        string fileName, string contentType, string externalSourceId, DateTimeOffset receivedAt, CancellationToken cancellationToken) =>
        await ReceiveCoreAsync(familyId, null, false, content, length, fileName, contentType, FinancialDocumentSource.Email,
            externalSourceId, null, null, receivedAt, cancellationToken);

    private async Task<FinancialDocumentInboxReceiveResult> ReceiveCoreAsync(Guid familyId, Guid? userId, bool requireMembership, Stream content, long length,
        string fileName, string contentType, FinancialDocumentSource source, string? externalSourceId, Guid? selectedCardId,
        string? password, DateTimeOffset? receivedAt, CancellationToken cancellationToken)
    {
        var authorized = requireMembership
            ? userId is { } memberId && await repository.CanAccessFamilyAsync(familyId, memberId, cancellationToken)
            : await repository.FamilyExistsAsync(familyId, cancellationToken);
        if (!authorized) return new("NotFound", null);
        if (length <= 0 || length > CreditCardPdfPreviewService.MaxFileBytes || !fileName.EndsWith(".pdf", StringComparison.OrdinalIgnoreCase))
            return Invalid("file", length <= 0 ? "O arquivo PDF está vazio." : length > CreditCardPdfPreviewService.MaxFileBytes ? "O arquivo excede o limite de 10 MB." : "O arquivo deve ter extensão .pdf.");
        if (password?.Length > 128) return Invalid("password", "A senha informada excede o limite permitido.");
        var safeFileName = Path.GetFileName(fileName.Replace('\\', '/'));
        if (safeFileName.Length is 0 or > 255) return Invalid("file", "O nome do arquivo é inválido ou excede 255 caracteres.");
        if (externalSourceId?.Trim().Length > 300) return Invalid("externalSourceId", "O identificador externo excede 300 caracteres.");
        using var buffer = new MemoryStream((int)length);
        await content.CopyToAsync(buffer, cancellationToken);
        var hash = Convert.ToHexStringLower(SHA256.HashData(buffer.ToArray()));
        if (!string.IsNullOrWhiteSpace(externalSourceId) && await repository.FindByExternalIdAsync(familyId, source, externalSourceId.Trim(), cancellationToken) is { } external)
            return external.ContentHash == hash ? new("AlreadyExists", Map(external)) : new("Conflict", Map(external));
        if (await repository.FindByHashAsync(familyId, hash, cancellationToken) is { } known) return new("AlreadyExists", Map(known));

        var id = Guid.NewGuid();
        buffer.Position = 0;
        var reference = await storage.SaveAsync(familyId, id, buffer, cancellationToken);
        var now = clock.GetUtcNow();
        var item = FinancialDocumentInboxItem.Create(id, familyId, source, externalSourceId, safeFileName, hash,
            "application/pdf", reference, receivedAt ?? now, now);
        try
        {
            if (!await repository.TryAddAsync(item, cancellationToken))
            {
                await storage.DeleteAsync(reference, CancellationToken.None);
                var concurrent = await repository.FindByHashAsync(familyId, hash, cancellationToken)
                    ?? (!string.IsNullOrWhiteSpace(externalSourceId) ? await repository.FindByExternalIdAsync(familyId, source, externalSourceId.Trim(), cancellationToken) : null);
                return concurrent is null ? new("Conflict", null) : new(concurrent.ContentHash == hash ? "AlreadyExists" : "Conflict", Map(concurrent));
            }
        }
        catch { await storage.DeleteAsync(reference, CancellationToken.None); throw; }
        if (userId is { } processingUser)
            await ProcessCoreAsync(item, processingUser, selectedCardId, password, cancellationToken);
        else
            await ProcessAutomationCoreAsync(item, cancellationToken);
        return new("Created", Map(item));
    }

    private async Task ProcessAutomationCoreAsync(FinancialDocumentInboxItem item, CancellationToken cancellationToken)
    {
        item.StartAnalysis(clock.GetUtcNow()); await repository.SaveAsync(cancellationToken);
        await using var stored = await storage.OpenReadAsync(item.StorageReference, cancellationToken);
        if (stored is null) { item.Fail("StorageObjectMissing", clock.GetUtcNow()); await repository.SaveAsync(cancellationToken); return; }
        try
        {
            var document = await extractor.ExtractAsync(stored, CreditCardPdfPreviewService.MaxPages, [], cancellationToken);
            if (document.Pages.Count == 0 || document.Pages.All(page => string.IsNullOrWhiteSpace(page.Text)))
            {
                if (classificationOptions.Enabled)
                {
                    item.ApplyClassification("Unknown", "Low", "Unknown", "InsufficientEvidence", clock.GetUtcNow());
                    item.RequireReview("InsufficientEvidence", clock.GetUtcNow());
                }
                else item.Fail("TextUnavailable", clock.GetUtcNow());
                await repository.SaveAsync(cancellationToken); return;
            }
            if (classificationOptions.Enabled)
            {
                var classification = await classifier.ClassifyAsync(new(item.OriginalFileName, document), cancellationToken);
                item.ApplyClassification(classification.DocumentType, classification.Confidence, classification.ClassificationSource, classification.FailureCode, clock.GetUtcNow());
                var route = analysisRouter.Route(classification);
                if (route != FinancialDocumentAnalysisPipeline.CreditCard)
                {
                    item.RequireReview(route == FinancialDocumentAnalysisPipeline.AwaitingExtractor ? "RecognizedDocumentWithoutExtractor" : classification.FailureCode ?? "InsufficientEvidence", clock.GetUtcNow());
                    await repository.SaveAsync(cancellationToken); return;
                }
            }
            var preview = extractor.Parse(document, item.OriginalFileName, null, null);
            var resolution = await resolver.ResolveAsync(item.FamilyId, preview.Statement.Issuer, preview.Statement.CardLastDigits, null, cancellationToken);
            var cardId = resolution.Status == CreditCardStatementResolutionStatus.Matched ? resolution.CardId : null;
            var ready = preview.Parser != "Generic" && preview.Reconciliation?.Status == "Reconciled" && cardId is not null;
            item.CompleteAnalysis(preview.Parser, preview.Statement.Competence, cardId,
                ready ? FinancialDocumentInboxStatus.ReadyToConfirm : FinancialDocumentInboxStatus.NeedsReview, clock.GetUtcNow());
            await repository.SaveAsync(cancellationToken);
        }
        catch (CreditCardPdfReadException exception)
        {
            if (classificationOptions.Enabled) item.ApplyClassification("Unknown", "Low", "Unknown", exception.Failure.ToString(), clock.GetUtcNow());
            if (exception.Failure is CreditCardPdfReadFailure.PasswordRequired or CreditCardPdfReadFailure.PasswordRejected)
                item.RequireReview(exception.Failure.ToString(), clock.GetUtcNow());
            else item.Fail(exception.Failure.ToString(), clock.GetUtcNow());
            await repository.SaveAsync(cancellationToken);
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            item.Fail("AnalysisFailed", clock.GetUtcNow()); await repository.SaveAsync(cancellationToken);
        }
    }

    public async Task<FinancialDocumentInboxDto?> ReprocessAsync(Guid familyId, Guid id, Guid userId, Guid? selectedCardId, string? password, CancellationToken cancellationToken)
    {
        var item = await repository.GetAsync(familyId, id, userId, cancellationToken);
        if (item is null) return null;
        await ProcessCoreAsync(item, userId, selectedCardId ?? item.CreditCardId, password, cancellationToken);
        return Map(item);
    }

    public async Task<IReadOnlyList<FinancialDocumentInboxDto>?> ListAsync(Guid familyId, Guid userId, CancellationToken cancellationToken)
    {
        if (!await repository.CanAccessFamilyAsync(familyId, userId, cancellationToken)) return null;
        return (await repository.ListAsync(familyId, userId, cancellationToken)).Select(Map).ToList();
    }

    public async Task<FinancialDocumentInboxDto?> IgnoreAsync(Guid familyId, Guid id, Guid userId, CancellationToken cancellationToken)
    {
        var item = await repository.GetAsync(familyId, id, userId, cancellationToken); if (item is null) return null;
        item.Ignore(clock.GetUtcNow()); await repository.SaveAsync(cancellationToken); return Map(item);
    }

    private async Task ProcessCoreAsync(FinancialDocumentInboxItem item, Guid userId, Guid? selectedCardId, string? password, CancellationToken cancellationToken)
    {
        item.StartAnalysis(clock.GetUtcNow()); await repository.SaveAsync(cancellationToken);
        await using var stored = await storage.OpenReadAsync(item.StorageReference, cancellationToken);
        if (stored is null) { item.Fail("StorageObjectMissing", clock.GetUtcNow()); await repository.SaveAsync(cancellationToken); return; }
        try
        {
            CreditCardPdfExtractedDocument? classifiedDocument = null;
            if (classificationOptions.Enabled)
            {
                var passwords = string.IsNullOrEmpty(password) ? Array.Empty<string>() : [password];
                classifiedDocument = await extractor.ExtractAsync(stored, CreditCardPdfPreviewService.MaxPages, passwords, cancellationToken);
                var classification = classifiedDocument.Pages.Count == 0 || classifiedDocument.Pages.All(page => string.IsNullOrWhiteSpace(page.Text))
                    ? new FinancialDocumentClassificationResult("Unknown", "Low", [], "Unknown", "InsufficientEvidence")
                    : await classifier.ClassifyAsync(new(item.OriginalFileName, classifiedDocument), cancellationToken);
                item.ApplyClassification(classification.DocumentType, classification.Confidence, classification.ClassificationSource, classification.FailureCode, clock.GetUtcNow());
                var route = analysisRouter.Route(classification);
                if (route != FinancialDocumentAnalysisPipeline.CreditCard)
                {
                    item.RequireReview(route == FinancialDocumentAnalysisPipeline.AwaitingExtractor ? "RecognizedDocumentWithoutExtractor" : classification.FailureCode ?? "InsufficientEvidence", clock.GetUtcNow());
                    await repository.SaveAsync(cancellationToken); return;
                }
                if (stored.CanSeek) stored.Position = 0;
            }
            CreditCardPdfPreview preview;
            if (selectedCardId is { } cardId)
            {
                var result = await previewService.PreviewAsync(item.FamilyId, cardId, userId, stored, stored.Length,
                    item.OriginalFileName, null, null, null, password, cancellationToken);
                if (result.Access != CreditCardPdfPreviewAccess.Allowed) { item.Fail("PdfAnalysisFailed", clock.GetUtcNow()); await repository.SaveAsync(cancellationToken); return; }
                preview = result.Preview!;
            }
            else
            {
                var passwords = string.IsNullOrEmpty(password) ? Array.Empty<string>() : [password];
                var document = classifiedDocument ?? await extractor.ExtractAsync(stored, CreditCardPdfPreviewService.MaxPages, passwords, cancellationToken);
                if (document.Pages.Count == 0 || document.Pages.All(page => string.IsNullOrWhiteSpace(page.Text))) { item.Fail("TextUnavailable", clock.GetUtcNow()); await repository.SaveAsync(cancellationToken); return; }
                preview = extractor.Parse(document, item.OriginalFileName, null, null);
                var resolution = await resolver.ResolveAsync(item.FamilyId, preview.Statement.Issuer, preview.Statement.CardLastDigits, userId, cancellationToken);
                selectedCardId = resolution.Status == CreditCardStatementResolutionStatus.Matched ? resolution.CardId : null;
            }
            var ready = preview.Parser != "Generic" && preview.Reconciliation?.Status == "Reconciled" && selectedCardId is not null;
            item.CompleteAnalysis(preview.Parser, preview.Statement.Competence, selectedCardId,
                ready ? FinancialDocumentInboxStatus.ReadyToConfirm : FinancialDocumentInboxStatus.NeedsReview, clock.GetUtcNow());
            await repository.SaveAsync(cancellationToken);
        }
        catch (CreditCardPdfReadException exception)
        {
            if (classificationOptions.Enabled) item.ApplyClassification("Unknown", "Low", "Unknown", exception.Failure.ToString(), clock.GetUtcNow());
            if (exception.Failure is CreditCardPdfReadFailure.PasswordRequired or CreditCardPdfReadFailure.PasswordRejected)
                item.RequireReview(exception.Failure.ToString(), clock.GetUtcNow());
            else item.Fail(exception.Failure.ToString(), clock.GetUtcNow());
            await repository.SaveAsync(cancellationToken);
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            item.Fail("AnalysisFailed", clock.GetUtcNow()); await repository.SaveAsync(cancellationToken);
        }
    }

    private static FinancialDocumentInboxReceiveResult Invalid(string field, string message) => new("Invalid", null, new Dictionary<string, string[]> { [field] = [message] });
    private static FinancialDocumentInboxDto Map(FinancialDocumentInboxItem item) => new(item.Id, item.Source.ToString(), item.ExternalSourceId,
        item.OriginalFileName, item.ContentHash, item.ContentType, item.ReceivedAt.ToString("O"), item.Status.ToString(), item.Parser,
        item.Competence, item.CreditCardId, item.FailureCode, item.UpdatedAt.ToString("O"), item.DocumentType, item.ClassificationConfidence,
        item.ClassificationSource, item.ClassificationFailureCode, item.ClassifiedAt?.ToString("O"));
}

public sealed class FinancialDocumentInboxReviewService(
    IFinancialDocumentInboxRepository repository,
    IFinancialDocumentStorage storage,
    ICreditCardPdfPreReconciliationService preReconciliation)
{
    public async Task<CreditCardPdfPreReconciliationResult> ReviewAsync(Guid familyId, Guid inboxItemId, Guid cardId,
        Guid userId, string? password, CancellationToken cancellationToken)
    {
        var item = await repository.GetAsync(familyId, inboxItemId, userId, cancellationToken);
        if (item is null) return new(CreditCardPdfPreviewAccess.NotFound, null, null);
        await using var content = await storage.OpenReadAsync(item.StorageReference, cancellationToken);
        if (content is null) return new(CreditCardPdfPreviewAccess.Invalid, null,
            new Dictionary<string, string[]> { ["document"] = ["O documento armazenado não está disponível."] });
        return await preReconciliation.PreReconcileAsync(familyId, cardId, userId, content, content.Length,
            item.OriginalFileName, null, item.Competence, null, password, cancellationToken);
    }
}

public sealed class FinancialDocumentInboxConfirmationService(
    IFinancialDocumentInboxRepository repository,
    IFinancialDocumentStorage storage,
    CreditCardPdfConfirmationService confirmation,
    TimeProvider clock)
{
    public async Task<CreditCardPdfConfirmationOperationResult> ConfirmAsync(Guid familyId, Guid inboxItemId, Guid cardId, Guid userId,
        string? password, CreditCardPdfConfirmationInput input, CancellationToken cancellationToken)
    {
        var item = await repository.GetAsync(familyId, inboxItemId, userId, cancellationToken);
        if (item is null) return new(CreditCardPdfConfirmationAccess.NotFound);
        await using var content = await storage.OpenReadAsync(item.StorageReference, cancellationToken);
        if (content is null) return new(CreditCardPdfConfirmationAccess.Invalid, null,
            new Dictionary<string, string[]> { ["document"] = ["O documento armazenado não está disponível."] });
        var result = await confirmation.ConfirmAsync(familyId, cardId, userId, content, content.Length, item.OriginalFileName,
            null, item.Competence, null, password, input, cancellationToken);
        if (result.Access == CreditCardPdfConfirmationAccess.Allowed)
        {
            item.MarkImported(cardId, clock.GetUtcNow());
            await repository.SaveAsync(cancellationToken);
        }
        return result;
    }
}
