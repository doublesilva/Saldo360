using System.Data;
using System.Security.Cryptography;
using Microsoft.EntityFrameworkCore;
using Saldo360.Application.CreditCards;
using Saldo360.Infrastructure.Persistence;

namespace Saldo360.Infrastructure.CreditCards;

public sealed record StoredStatementDiagnosticReport(string Status, string Source, string Provider, string Model,
    bool DatabaseReadOnly, Guid DocumentId, CreditCardStatementExtractionResult? Extraction = null,
    CreditCardStatementResolution? CardResolution = null, IReadOnlyList<StatementItemCategory>? Categories = null,
    string? FailureCode = null)
{
    public Guid? ContextCardId { get; init; }
    public string? ConsolidationBlockReason { get; init; }
    public string ConsolidationStatus { get; init; } = "NotExecuted";
}

/// <summary>Administrative, non-persisting preview only. Not registered in the API or called by deployment.</summary>
public sealed class StoredStatementDiagnostic(Saldo360DbContext db, CardEmailSearchAuthorization authorization,
    IFinancialDocumentInboxRepository documents, IFinancialDocumentStorage storage, IFinancialCreditCardRepository cards,
    IStatementCategoryContextRepository categories, ICreditCardStatementAgentPreviewService preview,
    CreditCardStatementResolver resolver, CreditCardStatementAgentOptions options)
{
    public async Task<StoredStatementDiagnosticReport> RunAsync(Guid workspace, Guid document, Guid candidate, Guid user,
        string? password, CancellationToken ct)
    {
        StoredStatementDiagnosticReport Report(string status, string code, bool readOnly = false) =>
            new(status, "None", "OpenAI", options.Model, readOnly, document, FailureCode: code);
        if (!options.Enabled || options.Mode != CreditCardStatementAgentMode.AgentPrimary || options.Provider != "OpenAI"
            || options.Model != "gpt-4o-mini" || options.Endpoint != "https://api.openai.com/v1/")
            return Report("Rejected", "ExplicitDiagnosticConfigurationRequired");
        if (db.Database.CurrentTransaction is not null || db.ChangeTracker.HasChanges()) return Report("Rejected", "DedicatedContextRequired");
        await using var tx = await db.Database.BeginTransactionAsync(IsolationLevel.RepeatableRead, ct);
        await db.Database.ExecuteSqlRawAsync("SET TRANSACTION READ ONLY", ct);
        var readOnly = await db.Database.SqlQueryRaw<string>("SELECT current_setting('transaction_read_only') AS \"Value\"").SingleAsync(ct) == "on";
        if (!readOnly) return Report("Rejected", "DatabaseNotReadOnly");
        if (!await authorization.CanSearchAsync(workspace, user, ct)) return Report("NotFound", "UnauthorizedContext", true);
        var item = await documents.GetAsync(workspace, document, user, ct);
        var card = await cards.GetAsync(workspace, candidate, ct);
        if (item is null || card is null || card.IsArchived) return Report("NotFound", "DocumentOrCardUnavailable", true);
        await using var file = await storage.OpenReadAsync(item.StorageReference, ct);
        if (file is null || !file.CanSeek || file.Length is <= 0 or > CreditCardPdfPreviewService.MaxFileBytes)
            return Report("Rejected", "StoredDocumentUnavailable", true);
        if (Convert.ToHexStringLower(await SHA256.HashDataAsync(file, ct)) != item.ContentHash)
            return Report("Rejected", "DocumentHashMismatch", true);
        file.Position = 0;
        var catalog = await categories.ReadAsync(workspace, candidate, document, ct);
        var context = new StatementCategoryContext(catalog.Categories.Where(c => c.IsActive && c.Type == Saldo360.Domain.Categories.CategoryType.Expense).Select(c => c.Name).ToArray(), catalog.History);
        var result = await preview.PreviewDiagnosticAsync(workspace, candidate, user, file, file.Length, item.OriginalFileName,
            item.Competence, password, context, ct);
        if (result.Access != CreditCardPdfPreviewAccess.Allowed || result.Preview is null)
            return Report("Rejected", result.FailureCode ?? "PdfAnalysisFailed", true);
        var extraction = result.Preview;
        var resolution = extraction.Primary is { } primary ? await resolver.ResolveDocumentAsync(workspace, primary.Header, ct) : null;
        var failure = result.FailureCode ?? extraction.AgentError;
        if (extraction.Primary is { } facts && (CreditCardStatementResolver.ContradictsSelectedContext(facts.Header, card.Issuer, card.LastFourDigits)
            || item.CreditCardId is { } associated && associated != candidate)) failure = "CardMismatch";
        var status = failure is null ? extraction.Assessment.ToString() : "NeedsReview";
        var suggestions = StatementCategorization.Categorize(extraction.Primary, catalog, new Dictionary<string, Guid>());
        if (db.ChangeTracker.HasChanges()) throw new InvalidOperationException("DiagnosticMutationDetected");
        await tx.RollbackAsync(ct);
        return new(status, extraction.PrimarySource == "Agent" && extraction.Primary is not null ? "OpenAI" : extraction.PrimarySource == "Parser" ? "Parser" : "None",
            "OpenAI", options.Model, true, document, extraction, resolution, suggestions, failure)
        { ContextCardId = candidate, ConsolidationBlockReason = failure ?? (extraction.DocumentWarnings?.Contains("IdentityEvidenceUnverified") == true ? "IdentityEvidenceUnverified"
            : resolution?.Status != CreditCardStatementResolutionStatus.Matched || resolution.CardId != candidate ? "CardIdentityUnresolved"
            : OfficialStatementAnalysisValidation.ParserReviewReason(extraction)) };
    }
}

/// <summary>Reuses existing storage path validation without creating directories or exposing writers.</summary>
public sealed class StatementDiagnosticReadOnlyStorage(string existingRoot) : IFinancialDocumentStorage
{
    private readonly IFinancialDocumentStorage inner = FileSystemFinancialDocumentStorage.ForMaintenance(existingRoot);
    public Task<Stream?> OpenReadAsync(string reference, CancellationToken ct) => inner.OpenReadAsync(reference, ct);
    public Task<string> SaveAsync(Guid workspace, Guid document, Stream content, CancellationToken ct) => throw new NotSupportedException("DiagnosticReadOnly");
    public Task DeleteAsync(string reference, CancellationToken ct) => throw new NotSupportedException("DiagnosticReadOnly");
}
