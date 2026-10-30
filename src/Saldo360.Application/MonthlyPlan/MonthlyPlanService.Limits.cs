using System.Globalization;
using Saldo360.Application.Budgets;
using Saldo360.Domain.Budgets;
using Saldo360.Domain.Planning;

namespace Saldo360.Application.MonthlyPlan;

public sealed partial class MonthlyPlanService
{
    // ---- Limites por categoria (teto de gastos) -------------------------------------------------------------------------------------------
    // Reutilizam as metas do orçamento existente (FinancialBudget/FinancialBudgetTarget): a execução usa as categorias atribuídas pelo Saldo360,
    // então reclassificar um lançamento ou uma compra atualiza o realizado do limite. O teto NUNCA é uma obrigação: não cria despesa nem previsão.

    /// <summary>Define ou revisa o limite mensal de uma categoria de despesa. Cria o orçamento da competência se ainda não existir; reativa um limite removido.</summary>
    public async Task<MonthlyPlanResult<CategoryLimitDto>> SetCategoryLimitAsync(Guid workspaceId, Guid userId, CategoryLimitRequest request, CancellationToken ct)
    {
        if (!await IsParticipantAsync(workspaceId, userId, ct)) return MonthlyPlanResult<CategoryLimitDto>.NotFound;
        if (!TryMonth(request.Competence, out var month)) return MonthlyPlanResult<CategoryLimitDto>.Invalid("competence", "Use uma competência válida no formato yyyy-MM.");
        if (request.CategoryId is not { } categoryId || categoryId == Guid.Empty) return MonthlyPlanResult<CategoryLimitDto>.Invalid("categoryId", "Informe a categoria de despesa.");
        if (request.Amount is null || !TryDecimal(request.Amount, out var amount)) return MonthlyPlanResult<CategoryLimitDto>.Invalid("amount", "Informe o limite como decimal com ponto.");
        if (await budgetRepository.GetCategoryAsync(workspaceId, categoryId, ct) is not { } category) return MonthlyPlanResult<CategoryLimitDto>.NotFound;
        if (!category.IsExpense) return MonthlyPlanResult<CategoryLimitDto>.Invalid("categoryId", "O limite deve referenciar uma categoria de despesa.");

        var budget = (await budgetRepository.ListAsync(workspaceId, month, true, ct)).FirstOrDefault();
        if (budget is { IsArchived: true }) return MonthlyPlanResult<CategoryLimitDto>.Conflict(BudgetResult.ArchivedMessage);
        if (budget is null)
        {
            var created = await budgetService.CreateAsync(workspaceId, userId, Key(month), null, ct);
            if (created.Access == BudgetAccess.Conflict) budget = (await budgetRepository.ListAsync(workspaceId, month, true, ct)).FirstOrDefault();
            else if (created.Access != BudgetAccess.Allowed) return From<CategoryLimitDto>(created);
            else budget = created.Budget;
        }
        if (budget is null) return MonthlyPlanResult<CategoryLimitDto>.Conflict(BudgetResult.DuplicateBudgetMessage);

        var existing = budget.Targets.FirstOrDefault(t => t.Category.Id == categoryId);
        BudgetResult saved;
        if (existing is null) saved = await budgetService.AddTargetAsync(workspaceId, budget.Id, userId, category.Name, amount, nameof(BudgetTargetGroupingType.Category), categoryId, ct, request.Note);
        else if (existing.IsArchived) saved = await budgetService.RestoreTargetAsync(workspaceId, budget.Id, existing.Id, userId, amount, ct, request.Note);
        else saved = await budgetService.UpdateTargetAsync(workspaceId, budget.Id, existing.Id, userId, existing.Name, amount, ct, request.Note);
        if (saved.Access != BudgetAccess.Allowed) return From<CategoryLimitDto>(saved);

        var facts = await repository.LoadAsync(workspaceId, month, month, ct);
        var limit = facts.Limits.First(l => l.CategoryId == categoryId);
        var itemized = MonthlyPlanCalculator.Itemized(facts, categoryId);
        return MonthlyPlanResult<CategoryLimitDto>.Ok(new(limit.TargetId, limit.CategoryId, limit.CategoryName, MonthlyPlanCalculator.Cents(limit.Amount),
            limit.OriginalAmount is { } o ? MonthlyPlanCalculator.Cents(o) : null, limit.OriginalAmount is { } original && original != limit.Amount, MonthlyPlanCalculator.Cents(itemized), itemized > limit.Amount, null));
    }

    /// <summary>Remove (arquiva) o limite da categoria na competência. O histórico permanece.</summary>
    public async Task<MonthlyPlanResult<CategoryLimitDto>> RemoveCategoryLimitAsync(Guid workspaceId, Guid userId, string? competence, Guid categoryId, string? note, CancellationToken ct)
    {
        if (!await IsParticipantAsync(workspaceId, userId, ct)) return MonthlyPlanResult<CategoryLimitDto>.NotFound;
        if (!TryMonth(competence, out var month)) return MonthlyPlanResult<CategoryLimitDto>.Invalid("competence", "Use uma competência válida no formato yyyy-MM.");
        var budget = (await budgetRepository.ListAsync(workspaceId, month, true, ct)).FirstOrDefault();
        var target = budget?.Targets.FirstOrDefault(t => t.Category.Id == categoryId);
        if (budget is null || target is null) return MonthlyPlanResult<CategoryLimitDto>.NotFound;
        var removed = await budgetService.ArchiveTargetAsync(workspaceId, budget.Id, target.Id, userId, ct, note);
        if (removed.Access != BudgetAccess.Allowed) return From<CategoryLimitDto>(removed);
        return MonthlyPlanResult<CategoryLimitDto>.Ok(new(target.Id, categoryId, target.Category.Name, MonthlyPlanCalculator.Cents(target.PlannedAmount), null, false, MonthlyPlanCalculator.Cents(0m), false, null));
    }

    // ---- Histórico -------------------------------------------------------------------------------------------------------------------------

    /// <summary>Alterações do planejamento da competência (linhas e limites), da mais recente para a mais antiga. <paramref name="subjectId"/> restringe a um item.</summary>
    public async Task<MonthlyPlanResult<IReadOnlyList<PlanHistoryDto>>> HistoryAsync(Guid workspaceId, Guid userId, string? competence, Guid? subjectId, CancellationToken ct)
    {
        if (!await IsParticipantAsync(workspaceId, userId, ct)) return MonthlyPlanResult<IReadOnlyList<PlanHistoryDto>>.NotFound;
        if (!TryMonth(competence, out var month)) return MonthlyPlanResult<IReadOnlyList<PlanHistoryDto>>.Invalid("competence", "Use uma competência válida no formato yyyy-MM.");
        var entries = await repository.HistoryAsync(workspaceId, month, subjectId, HistoryLimit, ct);
        return MonthlyPlanResult<IReadOnlyList<PlanHistoryDto>>.Ok(entries.Select(e => new PlanHistoryDto(e.Id, Key(e.Competence), e.Subject.ToString(), e.SubjectId, e.Action.ToString(), e.Label,
            e.PreviousAmount is { } p ? MonthlyPlanCalculator.Cents(p) : null, e.NewAmount is { } n ? MonthlyPlanCalculator.Cents(n) : null, e.Note, e.UserId, e.At)).ToList());
    }

    // ---- Cópia do mês anterior -------------------------------------------------------------------------------------------------------------

    /// <summary>
    /// Copia o planejamento (linhas e limites por categoria) de uma competência para outra, mediante confirmação explícita. Nunca copia lançamentos realizados,
    /// previsões, faturas nem metas de cartão. O destino não pode ter linhas ativas (nada é duplicado nem sobrescrito); limites já existentes no destino são mantidos.
    /// Linhas em conta ou categoria inativa são puladas. Receitas por hora são recalculadas pelo calendário do mês de destino (dias faturáveis limitados aos dias úteis).
    /// </summary>
    public async Task<MonthlyPlanResult<CopyPlanResultDto>> CopyAsync(Guid workspaceId, Guid userId, CopyPlanRequest request, CancellationToken ct)
    {
        if (!await IsParticipantAsync(workspaceId, userId, ct)) return MonthlyPlanResult<CopyPlanResultDto>.NotFound;
        if (!request.Confirm) return MonthlyPlanResult<CopyPlanResultDto>.Invalid("confirm", "Confirme a cópia do planejamento.");
        if (!TryMonth(request.FromCompetence, out var from)) return MonthlyPlanResult<CopyPlanResultDto>.Invalid("fromCompetence", "Use uma competência de origem válida no formato yyyy-MM.");
        if (!TryMonth(request.ToCompetence, out var to)) return MonthlyPlanResult<CopyPlanResultDto>.Invalid("toCompetence", "Use uma competência de destino válida no formato yyyy-MM.");
        if (from == to) return MonthlyPlanResult<CopyPlanResultDto>.Invalid("toCompetence", "A competência de destino deve ser diferente da de origem.");

        var source = await repository.LoadAsync(workspaceId, from, from, ct);
        if (source.Lines.Count == 0 && source.Limits.Count == 0) return MonthlyPlanResult<CopyPlanResultDto>.Invalid("fromCompetence", "A competência de origem não tem planejamento.");
        var destination = await repository.LoadAsync(workspaceId, to, to, ct);
        if (destination.Lines.Count > 0) return MonthlyPlanResult<CopyPlanResultDto>.Conflict("A competência de destino já tem linhas de planejamento; nada foi copiado.");

        var now = time.GetUtcNow();
        var all = (await accounts.ListAsync(workspaceId, true, ct)).ToDictionary(a => a.Id);
        var business = MonthlyPlanCalculator.CountDays(to, await NonWorkingAsync(workspaceId, to, ct)).BusinessDays;
        var lines = new List<FinancialPlanLine>(); var history = new List<FinancialPlanHistoryEntry>(); var adjusted = new List<Guid>(); var skippedLines = 0;
        var note = "Copiado de " + Key(from);
        foreach (var l in source.Lines)
        {
            bool Active(Guid? id) => id is not { } value || (all.TryGetValue(value, out var a) && a.IsActive);
            var categoryOk = l.CategoryId is not { } cid || (await budgetRepository.GetCategoryAsync(workspaceId, cid, ct)) is { IsActive: true };
            if (!Active(l.AccountId) || !Active(l.DestinationAccountId) || !categoryOk) { skippedLines++; continue; }

            var amount = l.Amount; var days = l.BillableDays; var clamped = false;
            if (l is { HourlyRate: { } rate, HoursPerDay: { } hours, BillableDays: { } sourceDays })
            {
                if (business == 0) { skippedLines++; continue; }
                days = Math.Min(sourceDays, business); clamped = days != sourceDays;
                amount = MonthlyPlanCalculator.HourlyAmount(rate, hours, days.Value);
            }
            DateOnly? expected = l.ExpectedDate is { } date ? new DateOnly(to.Year, to.Month, Math.Min(date.Day, DateTime.DaysInMonth(to.Year, to.Month))) : null;
            var copy = FinancialPlanLine.Create(workspaceId, to, new PlanLineDetails(l.Kind, l.Nature, l.Description, l.AccountId, l.DestinationAccountId, l.CategoryId, amount, expected, l.HourlyRate, l.HoursPerDay, days),
                userId, now, l.Id);
            lines.Add(copy);
            if (clamped) adjusted.Add(copy.Id);
            history.Add(FinancialPlanHistoryEntry.Create(workspaceId, to, PlanHistorySubject.Line, copy.Id, PlanHistoryAction.Copied, copy.Description, null, copy.Amount, note, userId, now));
        }

        var targetBudget = (await budgetRepository.ListAsync(workspaceId, to, true, ct)).FirstOrDefault();
        FinancialBudget? newBudget = null; var targets = new List<FinancialBudgetTarget>(); var skippedLimits = 0;
        if (source.Limits.Count > 0)
        {
            if (targetBudget is { IsArchived: true }) skippedLimits = source.Limits.Count;
            else
            {
                if (targetBudget is null) newBudget = FinancialBudget.Create(workspaceId, to, null, userId, now);
                var budgetId = targetBudget?.Id ?? newBudget!.Id;
                var taken = targetBudget?.Targets.Select(t => t.Category.Id).ToHashSet() ?? [];
                foreach (var limit in source.Limits)
                {
                    if (taken.Contains(limit.CategoryId) || await budgetRepository.GetCategoryAsync(workspaceId, limit.CategoryId, ct) is not { IsActive: true }) { skippedLimits++; continue; }
                    var target = FinancialBudgetTarget.Create(workspaceId, budgetId, limit.CategoryName, limit.Amount, limit.CategoryId, userId, now);
                    targets.Add(target);
                    history.Add(FinancialPlanHistoryEntry.Create(workspaceId, to, PlanHistorySubject.CategoryLimit, target.Id, PlanHistoryAction.Copied, limit.CategoryName, null, limit.Amount, note, userId, now));
                }
                if (targets.Count == 0) newBudget = null;
            }
        }
        if (lines.Count == 0 && targets.Count == 0) return MonthlyPlanResult<CopyPlanResultDto>.Conflict("Nada a copiar: as linhas e os limites de origem não podem ser aplicados ao destino.");

        if (!await repository.ApplyCopyAsync(new(lines, newBudget, targets, history), ct)) return MonthlyPlanResult<CopyPlanResultDto>.Conflict("O planejamento de destino foi alterado durante a cópia; tente novamente.");
        return MonthlyPlanResult<CopyPlanResultDto>.Ok(new(Key(from), Key(to), lines.Count, skippedLines, targets.Count, skippedLimits, adjusted));
    }
}
