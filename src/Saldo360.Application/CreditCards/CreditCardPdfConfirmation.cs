using System.Globalization;
using System.Security.Cryptography;
using System.Text;

namespace Saldo360.Application.CreditCards;

public sealed record CreditCardPdfCategoryDecision(string ComponentId, Guid CategoryId);
public sealed record CreditCardPdfConfirmationInput(bool Confirm, string DocumentId, IReadOnlyList<CreditCardPdfCategoryDecision> Categories);
public sealed record CreditCardPdfConfirmationItem(string ComponentId, string Type, string Result, Guid? EntityId);
public sealed record CreditCardPdfFinalReconciliation(string OfficialTotal, string PersistedTotal, string Difference);
public sealed record CreditCardPdfConfirmationResult(
    Guid ImportId, string DocumentId, string Status, string Parser, string? Competence,
    int CreatedPurchases, int CreatedHistoricalInstallments, int CreatedAdjustments,
    int AlreadyExisting, int Informational,
    IReadOnlyList<CreditCardPdfConfirmationItem> Items,
    CreditCardPdfFinalReconciliation FinalReconciliation);
public enum CreditCardPdfConfirmationAccess { Allowed, NotFound, Invalid, Conflict }
public sealed record CreditCardPdfConfirmationOperationResult(CreditCardPdfConfirmationAccess Access, CreditCardPdfConfirmationResult? Result = null, IDictionary<string, string[]>? Errors = null, string? Message = null);

public sealed record CreditCardPdfTrustedComponent(
    string ComponentId, int RowNumber, string Type, string Nature, DateOnly? Date,
    string Description, decimal? Amount, string Classification, Guid? ExistingEntityId, Guid? CategoryId);
public sealed record CreditCardPdfTrustedConfirmation(
    string DocumentHash, string DecisionHash, string Parser, string? Competence,
    decimal OfficialTotal, Guid InvoiceId, IReadOnlyList<CreditCardPdfTrustedComponent> Components);

public interface ICreditCardPdfConfirmationRepository
{
    Task<CreditCardPdfConfirmationOperationResult> ConfirmAsync(Guid familyId, Guid cardId, Guid userId, CreditCardPdfTrustedConfirmation confirmation, DateTimeOffset now, CancellationToken cancellationToken);
}

public sealed class CreditCardPdfConfirmationService(
    ICreditCardPdfPreReconciliationService preReconciliation,
    ICreditCardPdfConfirmationRepository repository,
    TimeProvider clock)
{
    public async Task<CreditCardPdfConfirmationOperationResult> ConfirmAsync(
        Guid familyId, Guid cardId, Guid userId, Stream pdf, long length, string fileName,
        string? issuerProfile, string? competence, string? dateFormat, string? password,
        CreditCardPdfConfirmationInput? input, CancellationToken cancellationToken)
    {
        if (input is null || !input.Confirm) return Invalid("confirm", "A confirmação explícita é obrigatória.");
        if (string.IsNullOrWhiteSpace(input.DocumentId)) return Invalid("documentId", "Informe o documentId revisado.");
        var preview = await preReconciliation.PreReconcileAsync(familyId, cardId, userId, pdf, length, fileName, issuerProfile, competence, dateFormat, password, cancellationToken);
        if (preview.Access == CreditCardPdfPreviewAccess.NotFound) return new(CreditCardPdfConfirmationAccess.NotFound);
        if (preview.Access == CreditCardPdfPreviewAccess.Invalid) return new(CreditCardPdfConfirmationAccess.Invalid, null, preview.Errors);
        var report = preview.Report!;
        if (!string.Equals(report.DocumentKey, input.DocumentId, StringComparison.Ordinal)) return new(CreditCardPdfConfirmationAccess.Conflict, null, null, "O arquivo recebido não corresponde ao documentId previamente revisado.");
        if (report.Parser == "Generic" || report.Reconciliation.Status != "Reconciled" || report.Reconciliation.OfficialTotal is null)
            return Invalid("file", "Somente PDFs processados por parser especializado e reconciliados podem ser confirmados.");
        if (report.Destination.Invoice != "Validated" || report.Destination.InvoiceId is null)
            return Invalid("invoice", "A fatura de destino precisa estar validada antes da confirmação.");
        var blocking = report.Items.Where(item => item.IncludedInCurrentTotal && item.Classification is "NeedsReview" or "Conflict" or "Unsupported").ToList();
        if (blocking.Count > 0) return Invalid("components", $"Existem {blocking.Count} componentes financeiros que exigem revisão ou possuem conflito.");

        var decisions = input.Categories.GroupBy(item => item.ComponentId).ToDictionary(group => group.Key, group => group.Last().CategoryId, StringComparer.Ordinal);
        var knownComponentIds = report.Items.Select(item => item.ComponentId).ToHashSet(StringComparer.Ordinal);
        if (decisions.Keys.Any(componentId => !knownComponentIds.Contains(componentId)))
            return Invalid("categories", "Existe decisão associada a componente que não pertence ao PDF reprocessado.");
        var trusted = new List<CreditCardPdfTrustedComponent>(report.Items.Count);
        foreach (var item in report.Items)
        {
            var purchase = item.Type is "Purchase" or "InternationalPurchase";
            Guid? categoryId = decisions.TryGetValue(item.ComponentId, out var selectedCategory) ? selectedCategory : null;
            if (item.Classification == "Create" && purchase && categoryId is null)
                return Invalid("categories", $"Informe uma categoria para o componente {item.ComponentId}.");
            if (!DateOnly.TryParseExact(item.TransactionDate, "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out var date)) date = default;
            decimal? amount = decimal.TryParse(item.Amount, NumberStyles.Number | NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture, out var parsed) ? parsed : null;
            trusted.Add(new(item.ComponentId, item.RowNumber, item.Type, item.Nature, date == default ? null : date,
                item.Description, amount, item.Classification, item.MatchedEntityId, categoryId));
        }
        var canonical = string.Join("\n", decisions.OrderBy(item => item.Key, StringComparer.Ordinal).Select(item => $"{item.Key}={item.Value:D}"));
        var decisionHash = Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(canonical)));
        var documentHash = report.DocumentKey["pdf-sha256:".Length..];
        var confirmation = new CreditCardPdfTrustedConfirmation(documentHash, decisionHash, report.Parser,
            report.Statement.Competence, decimal.Parse(report.Reconciliation.OfficialTotal, CultureInfo.InvariantCulture),
            report.Destination.InvoiceId.Value, trusted);
        return await repository.ConfirmAsync(familyId, cardId, userId, confirmation, clock.GetUtcNow(), cancellationToken);
    }

    private static CreditCardPdfConfirmationOperationResult Invalid(string field, string message) =>
        new(CreditCardPdfConfirmationAccess.Invalid, null, new Dictionary<string, string[]> { [field] = [message] });
}
