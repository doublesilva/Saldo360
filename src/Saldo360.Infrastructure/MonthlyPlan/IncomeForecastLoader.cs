using Microsoft.EntityFrameworkCore;
using Saldo360.Application.MonthlyPlan;
using Saldo360.Domain.Categories;
using Saldo360.Domain.Recurrences;
using Saldo360.Infrastructure.Persistence;

namespace Saldo360.Infrastructure.MonthlyPlan;

/// <summary>
/// PROJ-01: fonte ÚNICA da previsão de receita PJ. A projeção diária da Agenda e a visão integrada de seis meses leem daqui, então não há dois valores para o mesmo recebimento.
/// Somente leitura: calcula na hora, não grava nem cria lançamento. A receita realizada da conta no mês do recebimento substitui a estimativa
/// (<see cref="IncomeForecastCalculator.RealizedReplacementRatio"/>).
/// </summary>
internal static class IncomeForecastLoader
{
    private static readonly string[] TaxWords = ["DAS", "SIMPLES NACIONAL", "IMPOSTO", "TRIBUT", "DARF", "ISS ", "IRPJ", "CSLL"];

    public static async Task<IncomeForecastView> LoadAsync(Saldo360DbContext db, Guid workspaceId, DateOnly from, DateOnly through, CancellationToken ct)
    {
        var row = await db.FinancialIncomeForecasts.AsNoTracking().SingleOrDefaultAsync(f => f.WorkspaceId == workspaceId, ct);
        if (row is null) return IncomeForecastView.None;
        var settings = new IncomeForecastSettings(row.AccountId, row.HourlyRate, row.HoursPerDay, row.PaymentDay, row.PaymentOffsetMonths, row.TaxReservePercent, row.OperationalReserve, row.IsActive);

        var workedFrom = from.AddMonths(-settings.PaymentOffsetMonths - 2); var workedThrough = through.AddMonths(2);
        var overrides = (await db.FinancialIncomeForecastMonths.AsNoTracking().Where(m => m.WorkspaceId == workspaceId && m.Competence >= workedFrom && m.Competence <= workedThrough).ToListAsync(ct))
            .ToDictionary(m => m.Competence, m => new IncomeForecastMonthOverride(m.Competence, m.BillableDays, m.ExpectedAmount, m.PaymentDate, m.IsDismissed));
        var userNonWorking = await db.FinancialNonWorkingDays.AsNoTracking().Where(d => d.WorkspaceId == workspaceId && d.Date >= workedFrom && d.Date <= workedThrough.AddMonths(1)).Select(d => d.Date).ToListAsync(ct);

        // Receita realizada da conta por mês (a mesma regra dos indicadores: devolução bancária não é receita; transferência não é lançamento).
        var revenueRows = await Saldo360.Infrastructure.Transactions.FinancialTransactionKpi.Project(
                db.FinancialTransactions.AsNoTracking().Where(t => t.WorkspaceId == workspaceId && !t.IsCancelled && t.AccountId == settings.AccountId
                    && t.TransactionDate >= new DateOnly(from.Year, from.Month, 1).AddMonths(-1) && t.TransactionDate <= through))
            .Where(m => m.Revenue > 0m).Select(m => new { m.Date, m.Revenue }).ToListAsync(ct);
        var revenueByMonth = revenueRows.GroupBy(r => new DateOnly(r.Date.Year, r.Date.Month, 1)).ToDictionary(g => g.Key, g => g.Sum(r => r.Revenue));

        var occurrences = new List<IncomeForecastOccurrence>();
        foreach (var worked in IncomeForecastCalculator.WorkedMonthsFor(settings, from, through))
        {
            overrides.TryGetValue(worked, out var adjust);
            var payment = adjust?.PaymentDate ?? IncomeForecastCalculator.PaymentDateFor(settings, worked);
            if (payment < from || payment > through) continue;
            revenueByMonth.TryGetValue(new DateOnly(payment.Year, payment.Month, 1), out var realized);
            occurrences.Add(IncomeForecastCalculator.Occurrence(settings, worked, IncomeForecastCalculator.BusinessDays(worked, userNonWorking), adjust, realized));
        }
        return new(settings, occurrences, await TaxObligationMonthsAsync(db, workspaceId, settings.AccountId, new DateOnly(from.Year, from.Month, 1), through, ct));
    }

    private static bool IsTax(string description)
    {
        var text = " " + description.ToUpperInvariant() + " ";
        return TaxWords.Any(word => text.Contains(word.StartsWith("DAS") ? " DAS " : word, StringComparison.Ordinal));
    }

    /// <summary>Meses em que a conta de recebimento já tem despesa tributária cadastrada (recorrência ativa ou previsão): a reserva de tributos não se aplica de novo.</summary>
    private static async Task<IReadOnlySet<DateOnly>> TaxObligationMonthsAsync(Saldo360DbContext db, Guid workspaceId, Guid accountId, DateOnly from, DateOnly through, CancellationToken ct)
    {
        var months = new HashSet<DateOnly>();
        var recurrences = await db.FinancialRecurrences.AsNoTracking().Where(r => r.WorkspaceId == workspaceId && r.FinancialAccountId == accountId && r.Type == CategoryType.Expense
            && r.Status == RecurrenceStatus.Active && r.ArchivedAt == null).Select(r => new { r.Description, r.StartDate, r.EndDate }).ToListAsync(ct);
        foreach (var r in recurrences.Where(r => IsTax(r.Description)))
            for (var month = from; month <= through; month = month.AddMonths(1))
                if (new DateOnly(r.StartDate.Year, r.StartDate.Month, 1) <= month && (r.EndDate is not { } end || end >= month)) months.Add(month);
        var planned = await db.FinancialPlannedTransactions.AsNoTracking().Where(p => p.WorkspaceId == workspaceId && p.AccountId == accountId && p.Type == CategoryType.Expense && !p.IsCancelled
            && p.DueDate >= from && p.DueDate <= through).Select(p => new { p.Description, p.DueDate }).ToListAsync(ct);
        foreach (var p in planned.Where(p => IsTax(p.Description))) months.Add(new DateOnly(p.DueDate.Year, p.DueDate.Month, 1));
        return months;
    }
}
