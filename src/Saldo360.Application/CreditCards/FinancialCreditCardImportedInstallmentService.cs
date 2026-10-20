using System.Globalization;
using Saldo360.Application.Workspaces;

namespace Saldo360.Application.CreditCards;

public sealed class FinancialCreditCardImportedInstallmentService(
    IFinancialWorkspaceRepository workspaces,
    IFinancialCreditCardImportedInstallmentRepository repository)
{
    public async Task<ImportedInstallmentOperationResult> FindMatchesAsync(Guid workspaceId, Guid cardId, Guid userId, ImportedInstallmentInput? input, CancellationToken cancellationToken)
    {
        if (!await IsParticipant(workspaceId, userId, cancellationToken)) return new(ImportedInstallmentAccess.NotFound, null, null, null);
        if (!TryValidateInput(input, out var normalized, out var errors)) return new(ImportedInstallmentAccess.Invalid, null, null, errors);
        return new(ImportedInstallmentAccess.Allowed, null, await repository.FindMatchesAsync(workspaceId, cardId, normalized.Date, normalized.Amount, normalized.Number, normalized.Count, normalized.Competence, normalized.Description, cancellationToken), null);
    }

    public async Task<ImportedInstallmentOperationResult> LinkAsync(Guid workspaceId, Guid cardId, Guid userId, ImportedInstallmentInput? input, CancellationToken cancellationToken)
    {
        if (!await IsParticipant(workspaceId, userId, cancellationToken)) return new(ImportedInstallmentAccess.NotFound, null, null, null);
        if (!TryValidateInput(input, out var normalized, out var errors)) return new(ImportedInstallmentAccess.Invalid, null, null, errors);
        if (normalized.InstallmentId is null) return Invalid("installmentId", "Selecione explicitamente uma parcela existente.");
        if (!normalized.Confirm) return Invalid("confirm", "A confirmação explícita é obrigatória.");
        return await repository.LinkAsync(workspaceId, cardId, userId, normalized.ToInput(), DateTimeOffset.UtcNow, cancellationToken);
    }

    public async Task<ImportedInstallmentOperationResult> CreateHistoricalAsync(Guid workspaceId, Guid cardId, Guid userId, ImportedInstallmentInput? input, CancellationToken cancellationToken)
    {
        if (!await IsParticipant(workspaceId, userId, cancellationToken)) return new(ImportedInstallmentAccess.NotFound, null, null, null);
        if (!TryValidateInput(input, out var normalized, out var errors)) return new(ImportedInstallmentAccess.Invalid, null, null, errors);
        if (!normalized.Confirm) return Invalid("confirm", "A confirmação explícita é obrigatória.");
        if (normalized.Competence is null) return Invalid("competence", "A competência da fatura é obrigatória para uma parcela histórica.");
        if (normalized.CategoryId is { } category && !await repository.IsExpenseCategoryAsync(workspaceId, category, cancellationToken)) return Invalid("categoryId", "A categoria deve ser uma despesa ativa da família.");
        return await repository.CreateHistoricalAsync(workspaceId, cardId, userId, normalized.ToInput(), DateTimeOffset.UtcNow, cancellationToken);
    }

    public async Task<IReadOnlyList<ImportedInstallmentRecordDto>?> ListAsync(Guid workspaceId, Guid cardId, Guid userId, CancellationToken cancellationToken) =>
        await IsParticipant(workspaceId, userId, cancellationToken) ? await repository.ListAsync(workspaceId, cardId, cancellationToken) : null;

    public async Task<bool> CancelAsync(Guid workspaceId, Guid cardId, Guid id, Guid userId, CancellationToken cancellationToken)
    {
        if (!await IsParticipant(workspaceId, userId, cancellationToken)) return false;
        return await repository.CancelAsync(workspaceId, cardId, id, userId, DateTimeOffset.UtcNow, cancellationToken);
    }

    private async Task<bool> IsParticipant(Guid workspaceId, Guid userId, CancellationToken cancellationToken) => await workspaces.GetOwnerRoleAsync(workspaceId, userId, cancellationToken) is not null;

    private static bool TryValidateInput(ImportedInstallmentInput? input, out Normalized normalized, out IDictionary<string, string[]> errors)
    {
        var messages = new Dictionary<string, string[]>();
        if (input is null) { normalized = default; errors = new Dictionary<string, string[]> { ["body"] = ["Informe os dados da parcela."] }; return false; }
        var dateOk = DateOnly.TryParseExact(input.TransactionDate, "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out var date);
        var amountOk = decimal.TryParse(input.Amount, NumberStyles.AllowLeadingSign | NumberStyles.AllowDecimalPoint, CultureInfo.InvariantCulture, out var amount) && amount > 0 && decimal.Round(amount, 2) == amount;
        if (input.ImportId == Guid.Empty) messages["importId"] = ["Informe o identificador da operação."];
        if (input.RowNumber < 2) messages["rowNumber"] = ["Informe a linha original."];
        if (!dateOk) messages["transactionDate"] = ["A data deve estar no formato yyyy-MM-dd."];
        if (!amountOk) messages["amount"] = ["O valor deve ser positivo e ter até duas casas decimais."];
        if (string.IsNullOrWhiteSpace(input.Description)) messages["description"] = ["Informe a descrição."];
        if (input.InstallmentNumber < 1 || input.InstallmentCount < input.InstallmentNumber) messages["installment"] = ["Número e quantidade de parcelas incompatíveis."];
        if (input.Competence is not null && !DateOnly.TryParseExact($"{input.Competence}-01", "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out _)) messages["competence"] = ["A competência deve estar no formato yyyy-MM."];
        normalized = new(date, input.Description.Trim(), amount, input.InstallmentNumber, input.InstallmentCount, input.Competence, string.IsNullOrWhiteSpace(input.ExternalId) ? null : input.ExternalId.Trim(), input.CategoryId, input.Confirm, input.InstallmentId, input);
        errors = messages;
        return messages.Count == 0;
    }

    private static ImportedInstallmentOperationResult Invalid(string field, string message) => new(ImportedInstallmentAccess.Invalid, null, null, new Dictionary<string, string[]> { [field] = [message] });

    private readonly record struct Normalized(DateOnly Date, string Description, decimal Amount, int Number, int Count, string? Competence, string? ExternalId, Guid? CategoryId, bool Confirm, Guid? InstallmentId, ImportedInstallmentInput Original)
    {
        public ImportedInstallmentInput ToInput() => Original with { TransactionDate = Date.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture), Description = Description, Amount = Amount.ToString("0.00", CultureInfo.InvariantCulture), InstallmentNumber = Number, InstallmentCount = Count, Competence = Competence, ExternalId = ExternalId, CategoryId = CategoryId, InstallmentId = InstallmentId };
    }
}
