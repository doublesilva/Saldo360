using Microsoft.EntityFrameworkCore;
using Npgsql;
using Saldo360.Application.Planning;
using Saldo360.Domain.Planning;
using Saldo360.Infrastructure.Persistence;
using Saldo360.Infrastructure.Persistence.Configurations;

namespace Saldo360.Infrastructure.Planning;

internal sealed class FinancialPlanningScenarioRepository(Saldo360DbContext db) : IFinancialPlanningScenarioRepository
{
    public async Task AddScenarioAsync(FinancialPlanningScenario scenario, CancellationToken ct)
    {
        db.FinancialPlanningScenarios.Add(scenario);
        try { await db.SaveChangesAsync(ct); }
        catch (DbUpdateException e) when (e.InnerException is PostgresException { ConstraintName: FinancialPlanningScenarioConfiguration.UniqueNameIndex }) { db.ChangeTracker.Clear(); throw new InvalidOperationException("Já existe um cenário com este nome nesta família."); }
    }
    public Task<FinancialPlanningScenario?> FindScenarioAsync(Guid familyId, Guid id, CancellationToken ct) => db.FinancialPlanningScenarios.SingleOrDefaultAsync(x => x.FamilyId == familyId && x.Id == id, ct);
    public Task<bool> ScenarioNameExistsAsync(Guid familyId, string name, Guid? exceptId, CancellationToken ct) => db.FinancialPlanningScenarios.AnyAsync(x => x.FamilyId == familyId && x.Name == name && x.Id != exceptId, ct);
    public Task SaveAsync(CancellationToken ct) => db.SaveChangesAsync(ct);
    public Task<PlanningScenarioDto?> GetScenarioAsync(Guid familyId, Guid id, CancellationToken ct) => Project(db.FinancialPlanningScenarios.Where(x => x.FamilyId == familyId && x.Id == id)).SingleOrDefaultAsync(ct);
    public async Task<IReadOnlyList<PlanningScenarioDto>> ListScenariosAsync(Guid familyId, bool includeArchived, CancellationToken ct)
    {
        var query = db.FinancialPlanningScenarios.Where(x => x.FamilyId == familyId);
        if (!includeArchived) query = query.Where(x => x.Status != PlanningScenarioStatus.Archived);
        return await Project(query.OrderBy(x => x.StartMonth).ThenBy(x => x.Name)).ToListAsync(ct);
    }
    private static IQueryable<PlanningScenarioDto> Project(IQueryable<FinancialPlanningScenario> query) => query.Select(x => new PlanningScenarioDto(x.Id, x.FamilyId, x.RecoveryAccountId, x.Name, x.Description, x.StartMonth, x.Months, x.Status, x.CreatedAt, x.UpdatedAt, x.ArchivedAt));

    public async Task AddParameterAsync(FinancialPlanningMonthlyParameter parameter, CancellationToken ct)
    {
        db.FinancialPlanningMonthlyParameters.Add(parameter);
        try { await db.SaveChangesAsync(ct); }
        catch (DbUpdateException e) when (e.InnerException is PostgresException { ConstraintName: FinancialPlanningMonthlyParameterConfiguration.UniqueCompetenceIndex }) { db.ChangeTracker.Clear(); throw new InvalidOperationException("Já existe parâmetro para esta competência."); }
    }
    public Task<FinancialPlanningMonthlyParameter?> FindParameterAsync(Guid familyId, Guid scenarioId, DateOnly competence, CancellationToken ct) => db.FinancialPlanningMonthlyParameters.SingleOrDefaultAsync(x => x.FamilyId == familyId && x.ScenarioId == scenarioId && x.Competence == competence, ct);
    public async Task<IReadOnlyList<PlanningMonthlyParameterDto>> ListParametersAsync(Guid familyId, Guid scenarioId, CancellationToken ct) => await db.FinancialPlanningMonthlyParameters.AsNoTracking().Where(x => x.FamilyId == familyId && x.ScenarioId == scenarioId).OrderBy(x => x.Competence).Select(x => new PlanningMonthlyParameterDto(x.Id, x.FamilyId, x.ScenarioId, x.Competence, x.ExpectedAdditionalIncome, x.ExpectedAdditionalExpense, x.ContingencyReserve, x.RecoveryTargetPercentage, x.Notes)).ToListAsync(ct);
}
