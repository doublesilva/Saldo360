using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Saldo360.Application.Families;
using Saldo360.Domain.CreditCards;

namespace Saldo360.Application.CreditCards;

public sealed class FinancialCreditCardImportService(
    IFamilyRepository families,
    IFinancialCreditCardImportRepository imports,
    TimeProvider timeProvider)
{
    public const int MaxItems = 1_000;

    public async Task<CreditCardImportOperationResult> ConfirmAsync(
        Guid familyId,
        Guid cardId,
        Guid userId,
        CreditCardImportConfirmation? confirmation,
        CancellationToken cancellationToken)
    {
        if (await families.GetMemberRoleAsync(familyId, userId, cancellationToken) is null)
            return new(CreditCardImportAccess.NotFound, null, null);
        if (confirmation is null || !confirmation.Confirm)
            return Invalid("confirm", "A confirmação explícita é obrigatória.");
        if (confirmation.ImportId == Guid.Empty)
            return Invalid("importId", "Informe o identificador da operação.");
        var items = confirmation.Items;
        if (items is null || items.Count == 0) return Invalid("items", "Selecione ao menos um registro.");
        if (items.Count > MaxItems) return Invalid("items", $"A confirmação aceita no máximo {MaxItems} registros.");

        var errors = new Dictionary<string, string[]>();
        var normalized = new List<CreditCardImportItemInput>(items.Count);
        var rows = new HashSet<int>();
        var externalIds = new HashSet<string>(StringComparer.Ordinal);
        foreach (var item in items)
        {
            var itemErrors = new List<string>();
            if (item.RowNumber < 2 || !rows.Add(item.RowNumber)) itemErrors.Add("Número de linha inválido ou repetido.");
            if (!DateOnly.TryParseExact(item.TransactionDate, "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out _)) itemErrors.Add("Data deve estar normalizada como yyyy-MM-dd.");
            var description = item.Description?.Trim() ?? string.Empty;
            var amount = ParseMoney(item.Amount);
            var count = item.InstallmentCount ?? 1;
            var number = item.InstallmentNumber ?? 1;
            var total = string.IsNullOrWhiteSpace(item.TotalAmount) ? amount : ParseMoney(item.TotalAmount);
            if (amount is null) itemErrors.Add("O valor deve ser um decimal positivo com até duas casas.");
            if (CreditCardPurchaseDetails.Validate(description, total, count, out _) is { } validation) itemErrors.Add(validation);
            if (count == 1 && amount is { } oneAmount && total is { } oneTotal && oneAmount != oneTotal) itemErrors.Add("Para uma parcela, amount e totalAmount devem coincidir.");
            if (count > 1 && (number != 1 || string.IsNullOrWhiteSpace(item.TotalAmount))) itemErrors.Add("Parcela intermediária ou total original ausente não pode ser importada como compra completa.");
            if (number < 1 || number > count) itemErrors.Add("Número de parcela incompatível com a quantidade total.");
            if (!string.IsNullOrWhiteSpace(item.ExternalId) && !externalIds.Add(item.ExternalId.Trim())) itemErrors.Add("Identificador externo repetido na operação.");
            if (!await imports.IsExpenseCategoryAsync(familyId, item.CategoryId, cancellationToken)) itemErrors.Add("A categoria deve ser uma despesa ativa da família.");
            if (itemErrors.Count > 0) errors[$"row_{item.RowNumber}"] = itemErrors.ToArray();
            normalized.Add(item with { Description = description, Amount = amount?.ToString("0.00", CultureInfo.InvariantCulture) ?? item.Amount, TotalAmount = total?.ToString("0.00", CultureInfo.InvariantCulture), ExternalId = string.IsNullOrWhiteSpace(item.ExternalId) ? null : item.ExternalId.Trim(), InstallmentNumber = number, InstallmentCount = count });
        }
        if (errors.Count > 0) return new(CreditCardImportAccess.Invalid, null, errors);

        var hash = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(JsonSerializer.Serialize(new { familyId, cardId, items = normalized }))));
        var repositoryResult = await imports.ConfirmAsync(familyId, cardId, userId, confirmation.ImportId, hash, normalized, timeProvider.GetUtcNow(), cancellationToken);
        if (repositoryResult.Conflict) return new(CreditCardImportAccess.Conflict, null, null, repositoryResult.Message);
        if (repositoryResult.NotFound) return new(CreditCardImportAccess.NotFound, null, null);
        return new(CreditCardImportAccess.Allowed, repositoryResult.Result, null);
    }

    private static decimal? ParseMoney(string value)
    {
        return decimal.TryParse(value, NumberStyles.AllowLeadingSign | NumberStyles.AllowDecimalPoint, CultureInfo.InvariantCulture, out var amount) ? amount : null;
    }

    private static CreditCardImportOperationResult Invalid(string field, string message) =>
        new(CreditCardImportAccess.Invalid, null, new Dictionary<string, string[]> { [field] = [message] });
}

public sealed record CreditCardImportRepositoryResult(
    CreditCardImportResult? Result,
    bool Conflict,
    bool NotFound,
    string? Message = null);
