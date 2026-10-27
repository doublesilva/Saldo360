using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Saldo360.Domain.CreditCards;

namespace Saldo360.Application.CreditCards;

public sealed class InboxStatementAnalysisService(
    CardEmailSearchAuthorization authorization, IFinancialDocumentInboxRepository documents,
    IFinancialDocumentStorage storage, IFinancialCreditCardRepository cards,
    ICreditCardStatementAgentPreviewService preview, CreditCardStatementResolver resolver,
    IStatementCategoryContextRepository context, CreditCardStatementAgentOptions options,
    IFinancialEmailCollectorLock operationLock, TimeProvider clock)
{
    public async Task<InboxStatementAnalysisResult> GetAsync(Guid workspaceId, Guid documentId, Guid userId, CancellationToken ct)
    {
        if (!await authorization.CanSearchAsync(workspaceId, userId, ct)) return new("NotFound");
        var item = await documents.GetAsync(workspaceId, documentId, userId, ct);
        if (item is null) return new("NotFound");
        if (item.Status == FinancialDocumentInboxStatus.Analyzing) return new("Analyzing") { UpdatedAt = item.UpdatedAt };
        var analysis = Read(item.StatementAnalysisJson);
        return new(analysis?.Status ?? "NotAnalyzed", analysis) { UpdatedAt = item.UpdatedAt, Workflow = options.DocumentaryImport.Enabled ? StatementKnownInstallmentImport.Workflow : analysis?.Workflow };
    }

    public async Task<InboxStatementAnalysisResult> AnalyzeAsync(Guid workspaceId, Guid documentId, Guid cardId, Guid userId, string? password, CancellationToken ct, bool automatic = false)
    {
        if (!await authorization.CanSearchAsync(workspaceId, userId, ct)) return new("NotFound");
        await using var lease = await operationLock.TryAcquireAsync(workspaceId, $"statement-analysis:{documentId:N}", ct);
        if (lease is null) return new("Busy");
        var item = options.DocumentaryImport.Enabled
            ? await documents.GetForAnalysisAsync(workspaceId, documentId, userId, ct)
            : await documents.GetAsync(workspaceId, documentId, userId, ct);
        var card = await cards.GetAsync(workspaceId, cardId, ct);
        if (item is null || card is null || card.IsArchived) return new("NotFound");
        if (item.Status == FinancialDocumentInboxStatus.Imported && item.CreditCardId == cardId
            && Read(item.StatementAnalysisJson) is { Workflow: StatementKnownInstallmentImport.Workflow } confirmed && confirmed.CardId == cardId)
            return new("AlreadyImported", confirmed) { Workflow = confirmed.Workflow };
        if (item.Status is FinancialDocumentInboxStatus.Imported or FinancialDocumentInboxStatus.Ignored or FinancialDocumentInboxStatus.Conflict
            || (item.CreditCardId is { } associated && associated != cardId)) return new("Conflict");
        var catalog = await context.ReadAsync(workspaceId, cardId, documentId, ct);
        var previous = Read(item.StatementAnalysisJson);
        if (previous?.Workflow == StatementKnownInstallmentImport.Workflow && !options.DocumentaryImport.Enabled)
            return new("Unavailable", previous with { ConsolidationBlockReason = "DocumentaryImportDisabled",
                KnownInstallmentImport = new(false, "Blocked", [], ["DocumentaryImportDisabled"]) }) { Workflow = previous.Workflow };
        var hash = ComputeInputHash(item, card, catalog, options);
        if (item.Status is FinancialDocumentInboxStatus.ReadyForReview or FinancialDocumentInboxStatus.ReadyToConfirm
            && previous is { Status: "Reliable" } && previous.CardId == cardId && previous.InputHash == hash
            && (!automatic || previous.Extraction is { PrimarySource: "Agent" })) return new("AlreadyAnalyzed", previous);
        var manual = previous?.CardId == cardId ? previous.ManualOverrides : new Dictionary<string, Guid>();
        item.StartAnalysis(clock.GetUtcNow()); await documents.SaveAsync(ct);
        try
        {
            await using var file = await storage.OpenReadAsync(item.StorageReference, ct);
            if (file is null) return await FailAsync(item, cardId, hash, manual, "StorageObjectMissing", ct);
            if (options.DocumentaryImport.Enabled)
            {
                if (!file.CanSeek || file.Length <= 0 || file.Length > CreditCardPdfPreviewService.MaxFileBytes)
                    return await FailAsync(item, cardId, hash, manual, "StoredDocumentUnavailable", ct);
                var actual = Convert.ToHexStringLower(await SHA256.HashDataAsync(file, ct));
                if (actual != item.ContentHash) return await FailAsync(item, cardId, hash, manual, "DocumentHashMismatch", ct);
                file.Position = 0;
            }
            var categories = new StatementCategoryContext(catalog.Categories.Where(c => c.IsActive && c.Type == Saldo360.Domain.Categories.CategoryType.Expense).Select(c => c.Name).ToArray(), catalog.History);
            var result = automatic
                ? await preview.PreviewForProcessingAsync(workspaceId, cardId, userId, file, file.Length, item.OriginalFileName, item.Competence, password, categories, ct)
                : await preview.PreviewWithCategoriesAsync(workspaceId, cardId, userId, file, file.Length, item.OriginalFileName, item.Competence, password, categories, ct);
            if (result.Access != CreditCardPdfPreviewAccess.Allowed || result.Preview is null)
                return await FailAsync(item, cardId, hash, manual, result.FailureCode ?? "PdfAnalysisFailed", ct, result.PasswordResolutionCode);
            var extraction = result.Preview;
            var primary = extraction.Primary;
            // Resolution is only the existing consolidation guard; never the source of CardId or financial validity.
            var resolution = primary is null ? null : await resolver.ResolveDocumentAsync(workspaceId, primary.Header, ct);
            var mismatch = primary is not null && (CreditCardStatementResolver.ContradictsSelectedContext(primary.Header, card.Issuer, card.LastFourDigits)
                || result.FailureCode == "CardMismatch" || extraction.DocumentWarnings?.Contains("CardMismatch") == true);
            var status = extraction.Assessment.ToString();
            if (mismatch || automatic && extraction.PrimarySource != "Agent") status = "NeedsReview";
            var documentary = options.DocumentaryImport.Enabled;
            var suggestions = StatementCategorization.Categorize(primary, catalog, documentary ? new Dictionary<string, Guid>() : manual);
            if (!documentary && status == "Reliable" && suggestions.Any(c => c.Source is "InactiveCategory" or "ManualUnavailable" or "HistoryUnavailable" or "HistoryConflict")) status = "NeedsReview";
            var block = primary is null ? extraction.AgentError ?? "AnalysisRequired" : mismatch ? "CardMismatch" : extraction.DocumentWarnings?.Contains("IdentityEvidenceUnverified") == true ? "IdentityEvidenceUnverified"
                : resolution?.Status != CreditCardStatementResolutionStatus.Matched || resolution.CardId != cardId ? "CardIdentityUnresolved"
                : OfficialStatementAnalysisValidation.ParserReviewReason(extraction);
            var eligibility = documentary ? StatementKnownInstallmentImport.Evaluate(extraction, card.Issuer, card.LastFourDigits, card.DueDay) : null;
            if (documentary) block = eligibility!.Blockers.FirstOrDefault();
            var analysis = new InboxStatementAnalysis("2.0", cardId, hash, status, extraction, suggestions, manual,
                mismatch ? "CardMismatch" : extraction.AgentError)
            { ManualDecisions = !documentary && previous?.CardId == cardId ? previous.ManualDecisions : [], ConsolidationBlockReason = block,
                Workflow = documentary ? StatementKnownInstallmentImport.Workflow : null, KnownInstallmentImport = eligibility };
            item.SetStatementAnalysis(JsonSerializer.Serialize(analysis), status == "Reliable" && block is null, clock.GetUtcNow());
            await documents.SaveAsync(ct); return new(status, analysis);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        { await FailAsync(item, cardId, hash, manual, "AnalysisCancelled", CancellationToken.None); throw; }
        catch (Exception) { return await FailAsync(item, cardId, hash, manual, "StatementAnalysisFailed", ct); }
    }

    public async Task<InboxStatementAnalysisResult> CorrectCategoryAsync(Guid workspaceId, Guid documentId, string key, Guid categoryId, Guid userId, CancellationToken ct)
    {
        if (!await authorization.CanSearchAsync(workspaceId, userId, ct)) return new("NotFound");
        var item = await documents.GetAsync(workspaceId, documentId, userId, ct);
        if (item is null) return new("NotFound");
        if (item.Status is FinancialDocumentInboxStatus.Imported or FinancialDocumentInboxStatus.Ignored or FinancialDocumentInboxStatus.Conflict) return new("Conflict");
        await using var lease = await operationLock.TryAcquireAsync(workspaceId, $"statement-analysis:{documentId:N}", ct);
        if (lease is null) return new("Busy");
        var analysis = Read(item.StatementAnalysisJson);
        if (analysis?.Workflow == StatementKnownInstallmentImport.Workflow) return new("Conflict");
        if (analysis is null || !analysis.Categories.Any(c => c.Key == key)) return new("NotFound");
        var catalog = await context.ReadAsync(workspaceId, analysis.CardId, documentId, ct);
        var category = catalog.Categories.SingleOrDefault(c => c.Id == categoryId && c.IsActive && c.Type == Saldo360.Domain.Categories.CategoryType.Expense);
        if (category is null) return new("Invalid", Errors: new Dictionary<string, string[]> { ["categoryId"] = ["Selecione uma categoria de despesa ativa deste Personal."] });
        var manual = analysis.ManualOverrides.ToDictionary(x => x.Key, x => x.Value); manual[key] = categoryId;
        var itemIndex = analysis.Categories.Single(c => c.Key == key).ItemIndex;
        var purchase = analysis.Extraction?.Primary?.Items.ElementAtOrDefault(itemIndex);
        if (purchase is null) return new("Conflict");
        var decisions = analysis.ManualDecisions.Where(d => d.Key != key).Append(new(key, purchase.OriginalDescription, purchase.TransactionDate, purchase.Amount, categoryId)).ToArray();
        var updated = analysis with { ManualOverrides = manual, ManualDecisions = decisions, Categories = analysis.Categories.Select(c => c.Key == key ? c with { CategoryId = categoryId, CategoryName = category.Name, Confidence = "High", Source = "Manual", IsProposal = false } : c).ToArray() };
        item.SetStatementAnalysis(JsonSerializer.Serialize(updated), updated.Status == "Reliable" && updated.ConsolidationBlockReason is null, clock.GetUtcNow()); await documents.SaveAsync(ct);
        return new("Corrected", updated);
    }

    public static InboxStatementAnalysis? Read(string? json) => json is null ? null : JsonSerializer.Deserialize<InboxStatementAnalysis>(json);
    public static string ComputeInputHash(FinancialDocumentInboxItem item, FinancialCreditCardDto card, StatementCategoryCatalog catalog, CreditCardStatementAgentOptions options)
    {
        var cardId = card.Id;
        return Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(JsonSerializer.Serialize(new
        { item.ContentHash, cardId, card.LastFourDigits, card.Issuer, card.HolderUserId, card.ClosingDay, card.DueDay, options.Mode, options.Enabled, options.Model, options.Provider, options.Endpoint, options.DocumentaryImport, CreditCardStatementAgentPrompt.Version, CreditCardStatementAgentPrompt.DocumentaryVersion, catalog }))));
    }
    private async Task<InboxStatementAnalysisResult> FailAsync(FinancialDocumentInboxItem item, Guid cardId, string hash, IReadOnlyDictionary<string, Guid> manual, string code, CancellationToken ct, string? passwordResolutionCode = null)
    {
        var previous = Read(item.StatementAnalysisJson);
        var analysis = new InboxStatementAnalysis("2.0", cardId, hash, "Rejected", null, [], manual, code)
        { ManualDecisions = previous?.CardId == cardId ? previous.ManualDecisions : [], PasswordResolutionCode = passwordResolutionCode };
        item.SetStatementAnalysis(JsonSerializer.Serialize(analysis), false, clock.GetUtcNow()); await documents.SaveAsync(ct); return new("Rejected", analysis);
    }
}
