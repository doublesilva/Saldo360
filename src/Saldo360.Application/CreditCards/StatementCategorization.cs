using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Saldo360.Application.Categories;
using Saldo360.Domain.Categories;

namespace Saldo360.Application.CreditCards;

public sealed record StatementCategoryHistory(string Description, string TransactionDate, string Amount, string CategoryName, string Source = "ConfirmedPurchase");
public sealed record StatementCategoryContext(IReadOnlyList<string> ExistingCategories, IReadOnlyList<StatementCategoryHistory> History);
public sealed record StatementCategoryCatalog(IReadOnlyList<FinancialCategoryDto> Categories, IReadOnlyList<StatementCategoryHistory> History);
public interface IStatementCategoryContextRepository
{
    Task<StatementCategoryCatalog> ReadAsync(Guid workspaceId, Guid cardId, Guid excludedDocumentId, CancellationToken ct);
}
public sealed record StatementItemCategory(string Key, int ItemIndex, Guid? CategoryId, string CategoryName, bool IsProposal, string Confidence, string Source);
public sealed record StatementManualDecision(string Key, string Description, string? TransactionDate, string Amount, Guid CategoryId);
public sealed record InboxStatementAnalysis(string SchemaVersion, Guid CardId, string InputHash, string Status,
    CreditCardStatementExtractionResult? Extraction, IReadOnlyList<StatementItemCategory> Categories,
    IReadOnlyDictionary<string, Guid> ManualOverrides, string? FailureCode = null)
{
    public IReadOnlyList<StatementManualDecision> ManualDecisions { get; init; } = [];
}
public sealed record InboxStatementAnalysisResult(string Status, InboxStatementAnalysis? Analysis = null, IDictionary<string, string[]>? Errors = null);

public static class StatementCategorization
{
    public static string Normalize(string text) => string.Concat(text.Normalize(NormalizationForm.FormD)
        .Where(char.IsLetterOrDigit)).ToUpperInvariant();

    public static IReadOnlyList<StatementItemCategory> Categorize(CreditCardStatementAgentPreview? preview,
        StatementCategoryCatalog catalog, IReadOnlyDictionary<string, Guid> manual)
    {
        if (preview is null) return [];
        var categories = catalog.Categories.Where(c => c.Type == CategoryType.Expense).ToArray();
        var result = new List<StatementItemCategory>(); var occurrences = new Dictionary<string, int>();
        for (var index = 0; index < preview.Items.Count; index++)
        {
            var item = preview.Items[index];
            if (item.Type is not ("Purchase" or "Installment") || item.Nature != "Debit") continue;
            var identity = JsonSerializer.Serialize(new { Description = Normalize(item.OriginalDescription), item.TransactionDate, item.Amount, item.Nature, item.InstallmentNumber, item.InstallmentCount });
            var ordinal = occurrences.GetValueOrDefault(identity); occurrences[identity] = ordinal + 1;
            var key = Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(identity + ":" + ordinal)));
            var manualCategory = manual.TryGetValue(key, out var categoryId) ? categories.SingleOrDefault(c => c.Id == categoryId && c.IsActive) : null;
            if (manualCategory is not null) { result.Add(new(key, index, manualCategory.Id, manualCategory.Name, false, "High", "Manual")); continue; }
            if (manual.ContainsKey(key)) { result.Add(new(key, index, null, categories.SingleOrDefault(c => c.Id == categoryId)?.Name ?? "Categoria corrigida indisponível", false, "High", "ManualUnavailable")); continue; }
            var matchingHistory = catalog.History.Where(h => item.TransactionDate is not null && h.TransactionDate == item.TransactionDate
                && h.Amount == item.Amount && Normalize(h.Description) == Normalize(item.OriginalDescription)).ToArray();
            var reviewed = matchingHistory.Where(h => h.Source == "ManualReview").ToArray();
            var historic = (reviewed.Length > 0 ? reviewed : matchingHistory).Select(h => Normalize(h.CategoryName)).Distinct().ToArray();
            if (historic.Length > 1)
            { result.Add(new(key, index, null, "Histórico conflitante", false, "Low", "HistoryConflict")); continue; }
            var known = historic.Length == 1 ? categories.Where(c => c.IsActive && Normalize(c.Name) == historic[0]).ToArray() : [];
            if (known.Length == 1) { result.Add(new(key, index, known[0].Id, known[0].Name, false, "High", "History")); continue; }
            if (historic.Length == 1 && categories.Any(c => Normalize(c.Name) == historic[0] && !c.IsActive))
            { result.Add(new(key, index, null, categories.First(c => Normalize(c.Name) == historic[0]).Name, false, "High", "HistoryUnavailable")); continue; }
            var merchant = Normalize(item.OriginalDescription);
            var genericMerchant = new[] { "MERCADOLIVRE", "SHOPEE", "MERCADOPAGO", "PAGSEGURO", "PAYPAL" }.Any(merchant.Contains);
            var suggested = item.SuggestedCategory?.Trim();
            var secure = !genericMerchant && item.CategoryConfidence == "High" && item.Confidence == "High" && !string.IsNullOrWhiteSpace(suggested);
            var matches = secure ? categories.Where(c => Normalize(c.Name) == Normalize(suggested!)).ToArray() : [];
            if (matches.Length == 1 && matches[0].IsActive)
            { result.Add(new(key, index, matches[0].Id, matches[0].Name, false, "High", "Existing")); continue; }
            if (secure && matches.Length == 0 && suggested!.Length <= FinancialCategory.NameMaxLength && !suggested.Any(char.IsControl))
            { result.Add(new(key, index, null, suggested, true, "High", "Proposed")); continue; }
            var fallback = genericMerchant ? "Compras Online" : "Outros";
            var fallbackMatches = categories.Where(c => Normalize(c.Name) == Normalize(fallback)).ToArray();
            if (fallbackMatches.Length != 1 || !fallbackMatches[0].IsActive)
            {
                fallback = "Outros";
                fallbackMatches = categories.Where(c => Normalize(c.Name) == Normalize(fallback)).ToArray();
            }
            var existing = fallbackMatches.Length == 1 && fallbackMatches[0].IsActive ? fallbackMatches[0] : null;
            // An inactive matching name cannot be recreated. The proposal remains explicit for review.
            result.Add(new(key, index, existing?.Id, existing?.Name ?? fallback, existing is null && fallbackMatches.Length == 0, "Low", existing is null && fallbackMatches.Length > 0 ? "InactiveCategory" : "Generic"));
        }
        var proposals = new Dictionary<string, string>();
        return result.Select(c =>
        {
            if (!c.IsProposal) return c;
            var normalized = Normalize(c.CategoryName);
            if (!proposals.TryGetValue(normalized, out var name)) proposals[normalized] = name = c.CategoryName;
            return c with { CategoryName = name };
        }).ToArray();
    }
}
