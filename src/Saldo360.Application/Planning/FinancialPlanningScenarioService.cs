using System.Globalization;
using Saldo360.Application.Families;
using Saldo360.Application.Accounts;
using Saldo360.Domain.Common;
using Saldo360.Domain.Planning;

namespace Saldo360.Application.Planning;

public sealed class FinancialPlanningScenarioService(IFamilyRepository families, IFinancialPlanningScenarioRepository repository, IFinancialAccountRepository accounts, TimeProvider time)
{
    public async Task<PlanningScenarioResult> CreateAsync(Guid familyId, Guid userId, PlanningScenarioInput input, CancellationToken ct)
    {
        if (!await IsParticipant(familyId, userId, ct)) return new(PlanningScenarioAccess.NotFound, null, null);
        var errors = ValidateScenario(input, out var name, out var description, out var startMonth, out var months, out var recoveryAccountId);
        await ValidateRecoveryAccount(errors, familyId, recoveryAccountId, ct);
        if (errors.Count > 0) return new(PlanningScenarioAccess.Invalid, null, errors);
        if (await repository.ScenarioNameExistsAsync(familyId, name!, null, ct)) return new(PlanningScenarioAccess.Conflict, null, null, "Já existe um cenário com este nome nesta família.");
        var entity = FinancialPlanningScenario.Create(familyId, name!, description, startMonth, months, time.GetUtcNow(), recoveryAccountId);
        try { await repository.AddScenarioAsync(entity, ct); } catch (InvalidOperationException e) { return new(PlanningScenarioAccess.Conflict, null, null, e.Message); }
        return new(PlanningScenarioAccess.Allowed, await repository.GetScenarioAsync(familyId, entity.Id, ct), null);
    }

    public async Task<IReadOnlyList<PlanningScenarioDto>?> ListAsync(Guid familyId, Guid userId, bool includeArchived, CancellationToken ct) =>
        await IsParticipant(familyId, userId, ct) ? await repository.ListScenariosAsync(familyId, includeArchived, ct) : null;

    public async Task<PlanningScenarioDto?> GetAsync(Guid familyId, Guid id, Guid userId, CancellationToken ct) =>
        await IsParticipant(familyId, userId, ct) ? await repository.GetScenarioAsync(familyId, id, ct) : null;

    public async Task<PlanningScenarioResult> UpdateAsync(Guid familyId, Guid id, Guid userId, PlanningScenarioInput input, CancellationToken ct)
    {
        if (!await IsParticipant(familyId, userId, ct)) return new(PlanningScenarioAccess.NotFound, null, null);
        var entity = await repository.FindScenarioAsync(familyId, id, ct); if (entity is null) return new(PlanningScenarioAccess.NotFound, null, null);
        var errors = ValidateScenario(input, out var name, out var description, out var startMonth, out var months, out var recoveryAccountId);
        await ValidateRecoveryAccount(errors, familyId, recoveryAccountId, ct);
        if (errors.Count > 0) return new(PlanningScenarioAccess.Invalid, null, errors);
        if (await repository.ScenarioNameExistsAsync(familyId, name!, id, ct)) return new(PlanningScenarioAccess.Conflict, null, null, "Já existe um cenário com este nome nesta família.");
        try { entity.Update(name!, description, startMonth, months, time.GetUtcNow(), recoveryAccountId); await repository.SaveAsync(ct); }
        catch (InvalidOperationException e) { return new(PlanningScenarioAccess.Conflict, null, null, e.Message); }
        return new(PlanningScenarioAccess.Allowed, await repository.GetScenarioAsync(familyId, id, ct), null);
    }

    public async Task<PlanningScenarioResult> ArchiveAsync(Guid familyId, Guid id, Guid userId, CancellationToken ct)
    {
        if (!await IsParticipant(familyId, userId, ct)) return new(PlanningScenarioAccess.NotFound, null, null);
        var entity = await repository.FindScenarioAsync(familyId, id, ct); if (entity is null) return new(PlanningScenarioAccess.NotFound, null, null);
        entity.Archive(time.GetUtcNow()); await repository.SaveAsync(ct);
        return new(PlanningScenarioAccess.Allowed, await repository.GetScenarioAsync(familyId, id, ct), null);
    }

    public async Task<IReadOnlyList<PlanningMonthlyParameterDto>?> ListParametersAsync(Guid familyId, Guid scenarioId, Guid userId, CancellationToken ct)
    {
        if (!await IsParticipant(familyId, userId, ct) || await repository.GetScenarioAsync(familyId, scenarioId, ct) is null) return null;
        return await repository.ListParametersAsync(familyId, scenarioId, ct);
    }

    public async Task<PlanningParameterResult> UpsertParameterAsync(Guid familyId, Guid scenarioId, string competence, Guid userId, PlanningMonthlyParameterInput input, CancellationToken ct)
    {
        if (!await IsParticipant(familyId, userId, ct)) return new(PlanningScenarioAccess.NotFound, null, null);
        var scenario = await repository.FindScenarioAsync(familyId, scenarioId, ct); if (scenario is null) return new(PlanningScenarioAccess.NotFound, null, null);
        if (scenario.Status == PlanningScenarioStatus.Archived) return new(PlanningScenarioAccess.Conflict, null, null, "Cenário arquivado não pode receber parâmetros.");
        var errors = ValidateParameter(competence, input, scenario, out var month, out var values);
        if (errors.Count > 0) return new(PlanningScenarioAccess.Invalid, null, errors);
        var existing = await repository.FindParameterAsync(familyId, scenarioId, month, ct);
        if (existing is null)
        {
            var entity = FinancialPlanningMonthlyParameter.Create(familyId, scenarioId, month, values.income, values.expense, values.reserve, values.recovery, values.notes);
            await repository.AddParameterAsync(entity, ct);
            return new(PlanningScenarioAccess.Allowed, new(entity.Id, entity.FamilyId, entity.ScenarioId, entity.Competence, entity.ExpectedAdditionalIncome, entity.ExpectedAdditionalExpense, entity.ContingencyReserve, entity.RecoveryTargetPercentage, entity.Notes), null);
        }
        existing.Update(values.income, values.expense, values.reserve, values.recovery, values.notes); await repository.SaveAsync(ct);
        return new(PlanningScenarioAccess.Allowed, new(existing.Id, existing.FamilyId, existing.ScenarioId, existing.Competence, existing.ExpectedAdditionalIncome, existing.ExpectedAdditionalExpense, existing.ContingencyReserve, existing.RecoveryTargetPercentage, existing.Notes), null);
    }

    private static Dictionary<string, string[]> ValidateScenario(PlanningScenarioInput input, out string? name, out string? description, out DateOnly startMonth, out int months, out Guid? recoveryAccountId)
    {
        var errors = new Dictionary<string, string[]>(); name = input.Name?.Trim(); description = string.IsNullOrWhiteSpace(input.Description) ? null : input.Description.Trim(); startMonth = default; months = input.Months ?? 0; recoveryAccountId = input.RecoveryAccountId;
        if (string.IsNullOrWhiteSpace(name)) errors["name"] = ["Informe o nome do cenário."]; else if (name.Length > FinancialPlanningScenario.NameMaxLength) errors["name"] = ["O nome do cenário excede o limite."];
        if (description is { Length: > FinancialPlanningScenario.DescriptionMaxLength }) errors["description"] = ["A descrição excede o limite."];
        if (input.StartMonth is null || !DateOnly.TryParseExact($"{input.StartMonth}-01", "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out startMonth)) errors["startMonth"] = ["Informe uma competência válida no formato yyyy-MM."];
        if (months is < 1 or > 12) errors["months"] = ["O horizonte deve estar entre 1 e 12 meses."];
        return errors;
    }

    private async Task ValidateRecoveryAccount(Dictionary<string, string[]> errors, Guid familyId, Guid? accountId, CancellationToken ct)
    {
        if (accountId is not { } id) return;
        var account = await accounts.GetAsync(familyId, id, ct);
        if (account is null) errors["recoveryAccountId"] = ["A conta-alvo não pertence à família."];
        else if (!account.IsActive) errors["recoveryAccountId"] = ["A conta-alvo está arquivada ou inativa."];
    }

    private static Dictionary<string, string[]> ValidateParameter(string competence, PlanningMonthlyParameterInput input, FinancialPlanningScenario scenario, out DateOnly month, out (decimal income, decimal expense, decimal reserve, decimal recovery, string? notes) values)
    {
        var errors = new Dictionary<string, string[]>(); month = default; values = default;
        if (!DateOnly.TryParseExact($"{competence}-01", "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out month)) errors["competence"] = ["Informe uma competência válida no formato yyyy-MM."];
        else if (month < scenario.StartMonth || month >= scenario.StartMonth.AddMonths(scenario.Months)) errors["competence"] = ["A competência deve estar dentro do horizonte do cenário."];
        var income = ParseMoney(input.ExpectedAdditionalIncome, "expectedAdditionalIncome", errors); var expense = ParseMoney(input.ExpectedAdditionalExpense, "expectedAdditionalExpense", errors); var reserve = ParseMoney(input.ContingencyReserve, "contingencyReserve", errors); var recovery = ParsePercent(input.RecoveryTargetPercentage, errors);
        var notes = string.IsNullOrWhiteSpace(input.Notes) ? null : input.Notes.Trim(); if (notes is { Length: > FinancialPlanningMonthlyParameter.NotesMaxLength }) errors["notes"] = ["As notas excedem o limite."];
        values = (income, expense, reserve, recovery, notes); return errors;
    }

    private static decimal ParseMoney(string? text, string key, IDictionary<string, string[]> errors)
    {
        if (string.IsNullOrWhiteSpace(text)) { errors[key] = ["Informe o valor explicitamente; zero deve ser enviado como 0.00."]; return 0m; }
        if (!decimal.TryParse(text, NumberStyles.AllowDecimalPoint, CultureInfo.InvariantCulture, out var value) || value < 0 || Money.Validate(value, "O valor") is not null) errors[key] = ["Informe um valor decimal não negativo com até duas casas."];
        return value;
    }
    private static decimal ParsePercent(string? text, IDictionary<string, string[]> errors)
    {
        if (string.IsNullOrWhiteSpace(text)) { errors["recoveryTargetPercentage"] = ["Informe o percentual explicitamente; zero deve ser enviado como 0.00."]; return 0m; }
        if (!decimal.TryParse(text, NumberStyles.AllowDecimalPoint, CultureInfo.InvariantCulture, out var value) || value is < 0 or > 100 || value.Scale > 2) errors["recoveryTargetPercentage"] = ["O percentual deve estar entre 0 e 100, com até duas casas."];
        return value;
    }
    private async Task<bool> IsParticipant(Guid familyId, Guid userId, CancellationToken ct) => await families.GetMemberRoleAsync(familyId, userId, ct) is not null;
}
