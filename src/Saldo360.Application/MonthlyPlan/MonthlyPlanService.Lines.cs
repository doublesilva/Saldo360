using Saldo360.Application.Accounts;
using Saldo360.Application.Budgets;
using Saldo360.Domain.Common;
using Saldo360.Domain.Planning;

namespace Saldo360.Application.MonthlyPlan;

public sealed partial class MonthlyPlanService
{
    // ---- Linhas do plano -----------------------------------------------------------------------------------------------------------------

    /// <summary>Cadastra uma receita, despesa ou transferência prevista. Não cria lançamento nem previsão: é meta do orçamento.</summary>
    public async Task<MonthlyPlanResult<PlanLineDto>> CreateLineAsync(Guid workspaceId, Guid userId, PlanLineRequest request, CancellationToken ct)
    {
        if (!await IsParticipantAsync(workspaceId, userId, ct)) return MonthlyPlanResult<PlanLineDto>.NotFound;
        if (!TryMonth(request.Competence, out var month)) return MonthlyPlanResult<PlanLineDto>.Invalid("competence", "Use uma competência válida no formato yyyy-MM.");
        var built = await BuildDetailsAsync(workspaceId, month, null, null, request, ct);
        if (built.Errors.Count > 0) return MonthlyPlanResult<PlanLineDto>.Invalid(built.Errors);
        if (built.NotFound) return MonthlyPlanResult<PlanLineDto>.NotFound;

        var now = time.GetUtcNow();
        var line = FinancialPlanLine.Create(workspaceId, month, built.Details!, userId, now);
        repository.Add(line);
        repository.Stage(FinancialPlanHistoryEntry.Create(workspaceId, month, PlanHistorySubject.Line, line.Id, PlanHistoryAction.Created, line.Description, null, line.Amount, request.Note, userId, now));
        await repository.SaveAsync(ct);
        return MonthlyPlanResult<PlanLineDto>.Ok(await ToDtoAsync(workspaceId, line, ct));
    }

    /// <summary>Revisa o valor vigente e os dados da linha. O valor original e o histórico são preservados; sem alteração real nada é gravado.</summary>
    public async Task<MonthlyPlanResult<PlanLineDto>> ReviseLineAsync(Guid workspaceId, Guid userId, Guid lineId, PlanLineRequest request, CancellationToken ct)
    {
        if (!await IsParticipantAsync(workspaceId, userId, ct) || await repository.FindLineAsync(workspaceId, lineId, ct) is not { } line) return MonthlyPlanResult<PlanLineDto>.NotFound;
        if (line.IsArchived) return MonthlyPlanResult<PlanLineDto>.Conflict("A linha de planejamento removida não pode ser alterada.");
        var built = await BuildDetailsAsync(workspaceId, line.Competence, line.Kind, line, request, ct);
        if (built.Errors.Count > 0) return MonthlyPlanResult<PlanLineDto>.Invalid(built.Errors);
        if (built.NotFound) return MonthlyPlanResult<PlanLineDto>.NotFound;

        var current = new PlanLineDetails(line.Kind, line.Nature, line.Description, line.AccountId, line.DestinationAccountId, line.CategoryId, line.Amount, line.ExpectedDate,
            line.HourlyRate, line.HoursPerDay, line.BillableDays);
        if (built.Details! != current)
        {
            var now = time.GetUtcNow();
            var previous = line.Amount;
            line.Revise(built.Details!, userId, now);
            repository.Stage(FinancialPlanHistoryEntry.Create(workspaceId, line.Competence, PlanHistorySubject.Line, line.Id, PlanHistoryAction.Revised, line.Description, previous, line.Amount, request.Note, userId, now));
            await repository.SaveAsync(ct);
        }
        return MonthlyPlanResult<PlanLineDto>.Ok(await ToDtoAsync(workspaceId, line, ct));
    }

    /// <summary>Remove (arquiva) a linha. O registro e o histórico permanecem; remover duas vezes não repete o histórico.</summary>
    public async Task<MonthlyPlanResult<PlanLineDto>> RemoveLineAsync(Guid workspaceId, Guid userId, Guid lineId, string? note, CancellationToken ct)
    {
        if (!await IsParticipantAsync(workspaceId, userId, ct) || await repository.FindLineAsync(workspaceId, lineId, ct) is not { } line) return MonthlyPlanResult<PlanLineDto>.NotFound;
        var now = time.GetUtcNow();
        var previous = line.Amount;
        if (line.Archive(userId, now))
        {
            repository.Stage(FinancialPlanHistoryEntry.Create(workspaceId, line.Competence, PlanHistorySubject.Line, line.Id, PlanHistoryAction.Removed, line.Description, previous, null, note, userId, now));
            await repository.SaveAsync(ct);
        }
        return MonthlyPlanResult<PlanLineDto>.Ok(await ToDtoAsync(workspaceId, line, ct));
    }

    private async Task<PlanLineDto> ToDtoAsync(Guid workspaceId, FinancialPlanLine line, CancellationToken ct)
    {
        var lookups = await LookupsAsync(workspaceId, [line.CategoryId], ct);
        var business = MonthlyPlanCalculator.CountDays(line.Competence, await NonWorkingAsync(workspaceId, line.Competence, ct)).BusinessDays;
        return ToDto(ToFact(line), lookups, business);
    }

    private sealed record Built(PlanLineDetails? Details, Dictionary<string, string[]> Errors, bool NotFound);

    /// <summary>Valida o pedido e monta os dados da linha. Em revisão, <paramref name="existing"/> permite manter uma conta ou categoria que ficou inativa.</summary>
    private async Task<Built> BuildDetailsAsync(Guid workspaceId, DateOnly month, PlanLineKind? fixedKind, FinancialPlanLine? existing, PlanLineRequest r, CancellationToken ct)
    {
        var errors = new Dictionary<string, string[]>();
        PlanLineKind kind;
        if (fixedKind is { } locked)
        {
            kind = locked;
            if (r.Kind is not null && !string.Equals(r.Kind, kind.ToString(), StringComparison.Ordinal)) errors["kind"] = ["O tipo da linha não pode mudar. Remova a linha e cadastre outra."];
        }
        else if (!TryEnum(r.Kind, out kind)) errors["kind"] = ["Informe o tipo: Income, Expense ou Transfer."];

        PlanLineNature? nature = null;
        if (kind == PlanLineKind.Transfer)
        {
            if (r.Nature is not null) errors["nature"] = ["A transferência não tem natureza."];
        }
        else if (!TryEnum<PlanLineNature>(r.Nature, out var parsedNature)) errors["nature"] = ["Informe a natureza: Recurring, Fixed, Variable, Extraordinary ou Reserve."];
        else if (parsedNature == PlanLineNature.Reserve && kind != PlanLineKind.Expense) errors["nature"] = ["Reserva e provisão são somente despesas."];
        else nature = parsedNature;

        var description = r.Description?.Trim() ?? string.Empty;
        if (description.Length == 0) errors["description"] = ["Informe a descrição."];
        else if (description.Length > FinancialPlanLine.DescriptionMaxLength) errors["description"] = [$"A descrição deve ter no máximo {FinancialPlanLine.DescriptionMaxLength} caracteres."];

        var all = (await accounts.ListAsync(workspaceId, true, ct)).ToDictionary(a => a.Id);
        Guid? accountId = r.AccountId, destinationId = r.DestinationAccountId;
        FinancialAccountDto? Account(Guid? id, string field, Guid? current)
        {
            if (id is not { } value) return null;
            if (!all.TryGetValue(value, out var account)) { errors[field] = ["Conta não encontrada."]; return null; }
            if (!account.IsActive && value != current) errors[field] = ["A conta está inativa e não aceita novo planejamento."];
            return account;
        }
        Account(accountId, "accountId", existing?.AccountId);
        Account(destinationId, "destinationAccountId", existing?.DestinationAccountId);
        if (kind == PlanLineKind.Transfer)
        {
            if (accountId is null) errors["accountId"] = ["Informe a conta de origem da transferência."];
            if (destinationId is null) errors["destinationAccountId"] = ["Informe a conta de destino da transferência."];
            else if (destinationId == accountId) errors["destinationAccountId"] = ["As contas de origem e destino devem ser diferentes."];
        }
        else if (destinationId is not null) errors["destinationAccountId"] = ["Somente a transferência tem conta de destino."];

        Guid? categoryId = r.CategoryId;
        var notFound = false;
        if (kind == PlanLineKind.Transfer) { if (categoryId is not null) errors["categoryId"] = ["A transferência não tem categoria."]; }
        else if (categoryId is { } id)
        {
            if (await budgetRepository.GetCategoryAsync(workspaceId, id, ct) is not { } category) notFound = true;
            else if (kind == PlanLineKind.Expense != category.IsExpense) errors["categoryId"] = [kind == PlanLineKind.Expense ? "Use uma categoria de despesa." : "Use uma categoria de receita."];
            else if (!category.IsActive && id != existing?.CategoryId) errors["categoryId"] = ["A categoria está inativa."];
        }

        DateOnly? expected = r.ExpectedDate;
        if (expected is { } date && (date.Year != month.Year || date.Month != month.Month)) errors["expectedDate"] = ["A data esperada deve estar na competência do planejamento."];

        decimal? rate = null, hours = null; int? days = r.BillableDays;
        var hourlyRequested = r.HourlyRate is not null || r.HoursPerDay is not null || r.BillableDays is not null;
        decimal amount = 0m;
        if (hourlyRequested)
        {
            if (kind != PlanLineKind.Income) errors["hourlyRate"] = ["O cálculo por hora é somente para receitas."];
            else
            {
                if (r.HourlyRate is not null) rate = TryDecimal(r.HourlyRate, out var parsedRate) ? parsedRate : null;
                if (r.HoursPerDay is not null) hours = TryDecimal(r.HoursPerDay, out var parsedHours) ? parsedHours : null;
                var business = MonthlyPlanCalculator.CountDays(month, await NonWorkingAsync(workspaceId, month, ct)).BusinessDays;
                foreach (var (field, messages) in MonthlyPlanCalculator.ValidateHourly(rate, hours, days, business)) errors[field] = messages;
                if (errors.Count == 0)
                {
                    amount = MonthlyPlanCalculator.HourlyAmount(rate!.Value, hours!.Value, days!.Value);
                    if (r.Amount is not null && (!TryDecimal(r.Amount, out var informed) || informed != amount)) errors["amount"] = ["Na receita por hora o valor é calculado pelo sistema; omita o valor."];
                    else if (amount <= 0m) errors["amount"] = ["O valor esperado deve ser maior que zero."];
                    else if (Money.Validate(amount, "O valor esperado") is { } hourlyError) errors["amount"] = [hourlyError];
                }
            }
        }
        else if (r.Amount is null || !TryDecimal(r.Amount, out amount)) errors["amount"] = ["Informe o valor como decimal com ponto."];
        else if (amount <= 0m) errors["amount"] = ["O valor deve ser maior que zero."];
        else if (Money.Validate(amount, "O valor") is { } amountError) errors["amount"] = [amountError];

        if (errors.Count > 0 || notFound) return new(null, errors, notFound && errors.Count == 0);
        return new(new PlanLineDetails(kind, nature, description, accountId, destinationId, categoryId, amount, expected, rate, hours, days), errors, false);
    }
}
