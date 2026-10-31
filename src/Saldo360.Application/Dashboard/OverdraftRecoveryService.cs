using System.Globalization;
using Saldo360.Application.Accounts;
using Saldo360.Application.Dashboard.PlanActual;
using Saldo360.Application.Workspaces;
using Saldo360.Domain.Accounts;
using Saldo360.Domain.Common;

namespace Saldo360.Application.Dashboard;

public sealed record OverdraftRecoveryView(Guid AccountId, string AccountName, string Competence, string PeriodKind, string State,
    string? ContractedLimit, string OpeningUsed, string CurrentUsed, string? Available, string? AboveLimit, string Target,
    string Recovered, string? ProjectedRecovery, string? Deviation, string? PositiveBalance, DateOnly ReferenceDate,
    DateTimeOffset CapturedAt, DateTimeOffset UpdatedAt, IReadOnlyList<string> Reasons);

public interface IOverdraftRecoveryRepository
{
    Task<OverdraftRecoverySnapshot?> GetAsync(Guid workspaceId, Guid accountId, DateOnly competence, CancellationToken ct);
    Task<OverdraftRecoverySnapshot> AddIfAbsentAsync(OverdraftRecoverySnapshot snapshot, CancellationToken ct);
}

public sealed class OverdraftRecoveryService(IFinancialWorkspaceRepository workspaces, FinancialAccountService accounts,
    PlanActualService planActual, IOverdraftRecoveryRepository repository, TimeProvider time)
{
    public async Task<PlanActualResult<OverdraftRecoveryView>> CaptureAsync(Guid workspaceId, Guid userId, Guid accountId, string competence, CancellationToken ct)
    {
        if (await workspaces.GetOwnerRoleAsync(workspaceId, userId, ct) is null) return PlanActualResult<OverdraftRecoveryView>.NotFound;
        if (!TryMonth(competence, out var month)) return PlanActualResult<OverdraftRecoveryView>.Invalid("competence", "Use uma competência válida no formato yyyy-MM.");
        if (await repository.GetAsync(workspaceId, accountId, month, ct) is not null) return await GetAsync(workspaceId, userId, accountId, competence, ct);
        var plan = await planActual.GetAsync(workspaceId, userId, competence, PlanActualScopes.Account, accountId, ct);
        if (plan.Value is not { } dashboard) return new(plan.Access, null, plan.Errors);
        var overdraft = dashboard.Overdraft.Accounts.SingleOrDefault();
        if (overdraft?.UsedAtStart is null || dashboard.Situation.ProjectedClosing is null)
            return PlanActualResult<OverdraftRecoveryView>.Invalid("projection", "Não há histórico de abertura e projeção suficientes para registrar a meta. Consulte as ressalvas da projeção.");
        var openingUsed = Parse(overdraft.UsedAtStart);
        var closing = Parse(dashboard.Situation.ProjectedClosing);
        var quality = dashboard.Coverage.Complete && dashboard.SnapshotConsistent ? "Complete" : "Partial";
        var reasons = dashboard.Coverage.Reasons.Concat(overdraft.Reasons).Distinct().ToArray();
        var snapshot = OverdraftRecoverySnapshot.Create(workspaceId, accountId, month, -openingUsed, overdraft.StartAsOf ?? month,
            Parse(dashboard.Income.Projected), Parse(dashboard.Expenses.CashFlow.Projected), closing, quality, string.Join(",", reasons), time.GetUtcNow());
        await repository.AddIfAbsentAsync(snapshot, ct);
        return await GetAsync(workspaceId, userId, accountId, competence, ct);
    }

    public async Task<PlanActualResult<OverdraftRecoveryView>> GetAsync(Guid workspaceId, Guid userId, Guid accountId, string competence, CancellationToken ct)
    {
        if (!TryMonth(competence, out var month)) return PlanActualResult<OverdraftRecoveryView>.Invalid("competence", "Use uma competência válida no formato yyyy-MM.");
        var snapshot = await repository.GetAsync(workspaceId, accountId, month, ct);
        var account = await accounts.GetAsync(workspaceId, accountId, userId, ct);
        if (account is null) return PlanActualResult<OverdraftRecoveryView>.NotFound;
        if (snapshot is null) return PlanActualResult<OverdraftRecoveryView>.Invalid("snapshot", "A meta do mês ainda não foi registrada.");
        var today = BusinessCalendar.Today(time.GetUtcNow());
        var end = month.AddMonths(1).AddDays(-1);
        var asOf = today < month ? month : today > end ? end : today;
        var balanceResult = await accounts.GetBalanceAsync(workspaceId, accountId, userId, asOf, ct);
        if (balanceResult.Balance?.Balance is not { } balance) return PlanActualResult<OverdraftRecoveryView>.Invalid("balance", "Saldo indisponível para acompanhar a recuperação.");
        var openingUsed = Math.Max(0m, -snapshot.OpeningBalance);
        var currentUsed = Math.Max(0m, -balance);
        var recovered = Math.Min(openingUsed, openingUsed - currentUsed);
        decimal? projectedRecovery = null, deviation = null;
        if (asOf == today && PlanActualCalculator.PeriodKind(month, today) == "Current")
        {
            var plan = await planActual.GetAsync(workspaceId, userId, competence, PlanActualScopes.Account, accountId, ct);
            if (plan.Value?.Situation.ProjectedClosing is { } closing)
            {
                projectedRecovery = Math.Min(openingUsed, openingUsed - Math.Max(0m, -Parse(closing)));
                deviation = projectedRecovery - snapshot.RecoveryTarget;
            }
        }
        var available = account.OverdraftLimit is { } limit ? Math.Max(0m, limit - currentUsed) : (decimal?)null;
        var excess = account.OverdraftLimit is { } known ? Math.Max(0m, currentUsed - known) : (decimal?)null;
        var reasons = string.IsNullOrWhiteSpace(snapshot.Composition) ? [] : snapshot.Composition.Split(',', StringSplitOptions.RemoveEmptyEntries);
        var state = currentUsed == 0m ? "FullyRecovered" : recovered < 0m ? "Worsening" : excess > 0m ? "AboveLimit" : snapshot.Quality;
        return PlanActualResult<OverdraftRecoveryView>.Ok(new(account.Id, account.Name, competence, PlanActualCalculator.PeriodKind(month, today), state,
            F(account.OverdraftLimit), F(openingUsed)!, F(currentUsed)!, F(available), F(excess), F(snapshot.RecoveryTarget)!, F(recovered)!, F(projectedRecovery), F(deviation),
            balance > 0m ? F(balance) : null, snapshot.ReferenceDate, snapshot.CapturedAt, time.GetUtcNow(), reasons));
    }

    private static decimal Parse(string value) => decimal.Parse(value, CultureInfo.InvariantCulture);
    private static string? F(decimal? value) => value is { } amount ? Money.Format(amount) : null;
    private static bool TryMonth(string value, out DateOnly month)
    {
        month = default;
        return value is { Length: 7 } && value[4] == '-' && int.TryParse(value[..4], out var y) && int.TryParse(value[5..], out var m)
            && y is >= 2000 and <= 2100 && m is >= 1 and <= 12 && (month = new DateOnly(y, m, 1)) != default;
    }
}
