using Saldo360.Domain.Common;

namespace Saldo360.Application.MonthlyPlan;

public sealed partial class MonthlyPlanService
{
    // ---- Provisão de alimentação no cartão (Entrega 2) -------------------------------------------------------------------------------------------
    // Parâmetro do orçamento: valor mensal (inicialmente R$ 2.000), categorias alimentares por identificador e ajuste por competência. Estimativa de planejamento: nada gera lançamento.

    private async Task<FoodProvisionDto> BuildFoodDtoAsync(Guid workspaceId, CancellationToken ct)
    {
        var first = new DateOnly(Today.Year, Today.Month, 1);
        var facts = await repository.FoodProvisionAsync(workspaceId, first, first, ct);
        return new(MonthlyPlanCalculator.Cents(facts.DefaultAmount), facts.AmountConfigured, facts.CategoryIds.Order().ToList(), facts.CategoriesInferred,
            facts.MonthAmounts.OrderBy(x => x.Key).ToDictionary(x => Key(x.Key), x => MonthlyPlanCalculator.Cents(x.Value)));
    }

    public async Task<MonthlyPlanResult<FoodProvisionDto>> GetFoodProvisionAsync(Guid workspaceId, Guid userId, CancellationToken ct) =>
        await IsParticipantAsync(workspaceId, userId, ct) ? MonthlyPlanResult<FoodProvisionDto>.Ok(await BuildFoodDtoAsync(workspaceId, ct)) : MonthlyPlanResult<FoodProvisionDto>.NotFound;

    public async Task<MonthlyPlanResult<FoodProvisionDto>> SaveFoodProvisionAsync(Guid workspaceId, Guid userId, FoodProvisionRequest request, CancellationToken ct)
    {
        if (!await IsParticipantAsync(workspaceId, userId, ct)) return MonthlyPlanResult<FoodProvisionDto>.NotFound;
        var errors = new Dictionary<string, string[]>();
        if (!TryDecimal(request.MonthlyAmount, out var amount) || amount < 0m) errors["monthlyAmount"] = ["Informe o valor mensal, zero ou maior."];
        else if (Money.Validate(amount, "O valor mensal") is { } amountError) errors["monthlyAmount"] = [amountError];
        var categories = (request.CategoryIds ?? []).Distinct().ToList();
        if (categories.Count == 0) errors["categoryIds"] = ["Informe ao menos uma categoria de despesa alimentar."];
        else if (!await repository.AreExpenseCategoriesAsync(workspaceId, categories, ct)) errors["categoryIds"] = ["Use apenas categorias de despesa do orçamento."];
        if (errors.Count > 0) return MonthlyPlanResult<FoodProvisionDto>.Invalid(errors);
        await repository.UpsertFoodProvisionAsync(workspaceId, null, amount, categories, userId, time.GetUtcNow(), ct);
        return MonthlyPlanResult<FoodProvisionDto>.Ok(await BuildFoodDtoAsync(workspaceId, ct));
    }

    public async Task<MonthlyPlanResult<FoodProvisionDto>> SaveFoodProvisionMonthAsync(Guid workspaceId, Guid userId, string? competence, FoodProvisionMonthRequest request, CancellationToken ct)
    {
        if (!await IsParticipantAsync(workspaceId, userId, ct)) return MonthlyPlanResult<FoodProvisionDto>.NotFound;
        if (!TryMonth(competence, out var month)) return MonthlyPlanResult<FoodProvisionDto>.Invalid("competence", "Use uma competência válida no formato yyyy-MM.");
        if (!TryDecimal(request.MonthlyAmount, out var amount) || amount < 0m) return MonthlyPlanResult<FoodProvisionDto>.Invalid("monthlyAmount", "Informe o valor do mês, zero ou maior.");
        if (Money.Validate(amount, "O valor do mês") is { } amountError) return MonthlyPlanResult<FoodProvisionDto>.Invalid("monthlyAmount", amountError);
        await repository.UpsertFoodProvisionAsync(workspaceId, month, amount, [], userId, time.GetUtcNow(), ct);
        return MonthlyPlanResult<FoodProvisionDto>.Ok(await BuildFoodDtoAsync(workspaceId, ct));
    }

    public async Task<MonthlyPlanResult<FoodProvisionDto>> RemoveFoodProvisionMonthAsync(Guid workspaceId, Guid userId, string? competence, CancellationToken ct)
    {
        if (!await IsParticipantAsync(workspaceId, userId, ct)) return MonthlyPlanResult<FoodProvisionDto>.NotFound;
        if (!TryMonth(competence, out var month)) return MonthlyPlanResult<FoodProvisionDto>.Invalid("competence", "Use uma competência válida no formato yyyy-MM.");
        await repository.RemoveFoodProvisionMonthAsync(workspaceId, month, ct);
        return MonthlyPlanResult<FoodProvisionDto>.Ok(await BuildFoodDtoAsync(workspaceId, ct));
    }
}
