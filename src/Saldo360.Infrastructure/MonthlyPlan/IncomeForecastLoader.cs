using Microsoft.EntityFrameworkCore;
using Saldo360.Application.MonthlyPlan;
using Saldo360.Domain.Categories;
using Saldo360.Domain.Recurrences;
using Saldo360.Infrastructure.Persistence;

namespace Saldo360.Infrastructure.MonthlyPlan;

/// <summary>
/// PROJ-01: fonte ÚNICA da previsão de receita PJ. A projeção diária da Agenda e a visão integrada de seis meses leem daqui, então não há dois valores para o mesmo recebimento.
/// Somente leitura: calcula na hora, não grava nem cria lançamento. Só o recebimento com evidência (<see cref="IncomeForecastCalculator.Classify"/>) liquida a competência,
/// total ou parcialmente; o restante fica como residual ou como entrada não identificada.
/// </summary>
internal static class IncomeForecastLoader
{
    private static readonly string[] TaxWords = ["DAS", "SIMPLES NACIONAL", "IMPOSTO", "TRIBUT", "DARF", "ISS ", "IRPJ", "CSLL"];

    public static async Task<IncomeForecastView> LoadAsync(Saldo360DbContext db, Guid workspaceId, DateOnly from, DateOnly through, DateOnly today, CancellationToken ct)
    {
        var row = await db.FinancialIncomeForecasts.AsNoTracking().SingleOrDefaultAsync(f => f.WorkspaceId == workspaceId, ct);
        InferredIncome? inferred = null; IncomeForecastSettings settings;
        if (row is null)
        {
            var inference = await InferAsync(db, workspaceId, from, ct);
            if (inference is null) return IncomeForecastView.None;
            (settings, inferred) = inference.Value;
        }
        else settings = new IncomeForecastSettings(row.AccountId, row.HourlyRate, row.HoursPerDay, row.PaymentDay, row.PaymentOffsetMonths, row.TaxReservePercent, row.OperationalReserve, row.IsActive);

        var workedFrom = from.AddMonths(-settings.PaymentOffsetMonths - 2); var workedThrough = through.AddMonths(2);
        var overrides = (await db.FinancialIncomeForecastMonths.AsNoTracking().Where(m => m.WorkspaceId == workspaceId && m.Competence >= workedFrom && m.Competence <= workedThrough).ToListAsync(ct))
            .ToDictionary(m => m.Competence, m => new IncomeForecastMonthOverride(m.Competence, m.BillableDays, m.ExpectedAmount, m.PaymentDate, m.IsDismissed));
        if (inferred is not null)
            foreach (var worked in IncomeForecastCalculator.WorkedMonthsFor(settings, from, through))
                overrides[worked] = overrides.TryGetValue(worked, out var user) ? user with { ExpectedAmount = user.ExpectedAmount ?? inferred.MonthlyAmount } : new(worked, null, inferred.MonthlyAmount, null, false);
        var userNonWorking = await db.FinancialNonWorkingDays.AsNoTracking().Where(d => d.WorkspaceId == workspaceId && d.Date >= workedFrom && d.Date <= workedThrough.AddMonths(1)).Select(d => d.Date).ToListAsync(ct);

        // Entradas da conta (a mesma regra dos indicadores: devolução bancária não é receita; transferência não é lançamento), com descrição para a conciliação por evidência.
        // O histórico anterior (seis meses antes da janela) identifica o pagador; entrada sem pagador reconhecido nunca liquida a competência.
        var historyFrom = new DateOnly(from.Year, from.Month, 1).AddMonths(-1 - IncomeInference.WindowMonths);
        var receiptRows = await db.FinancialTransactions.AsNoTracking()
            .Where(t => t.WorkspaceId == workspaceId && !t.IsCancelled && t.AccountId == settings.AccountId && t.Type == CategoryType.Income
                && t.Origin != Saldo360.Domain.Transactions.FinancialTransactionOrigin.PluggyRefund && t.TransactionDate >= historyFrom && t.TransactionDate <= through)
            .Select(t => new IncomeReceipt(t.TransactionDate, t.Amount, t.Description)).ToListAsync(ct);

        // Cada recebimento vinculado é consumido uma única vez: competências com o mesmo mês de pagamento dividem o que entrou, da mais antiga para a mais nova. O histórico do pagador
        // diz QUEM pagou, não QUAL competência: se há mais de uma competência disputando o mesmo dinheiro (mesmo mês ou valor atrasado anterior), o vínculo é sinalizado como ambíguo.
        var pools = new Dictionary<DateOnly, IncomeReceiptEvidence>();
        var candidates = new List<(DateOnly Worked, DateOnly Payment, IncomeForecastMonthOverride? Adjust)>();
        foreach (var worked in IncomeForecastCalculator.WorkedMonthsFor(settings, from, through))
        {
            overrides.TryGetValue(worked, out var adjust);
            var payment = adjust?.PaymentDate ?? IncomeForecastCalculator.PaymentDateFor(settings, worked);
            if (payment >= from && payment <= through) candidates.Add((worked, payment, adjust));
        }
        var occurrences = new List<IncomeForecastOccurrence>();
        foreach (var (worked, payment, adjust) in candidates.OrderBy(c => c.Payment).ThenBy(c => c.Worked))
        {
            var paymentMonth = new DateOnly(payment.Year, payment.Month, 1);
            if (!pools.TryGetValue(paymentMonth, out var pool))
                pool = IncomeForecastCalculator.Classify(
                    receiptRows.Where(r => r.Date.Year == payment.Year && r.Date.Month == payment.Month),
                    receiptRows.Where(r => r.Date < paymentMonth && r.Date >= paymentMonth.AddMonths(-IncomeInference.WindowMonths)));
            var occurrence = IncomeForecastCalculator.Occurrence(settings, worked, IncomeForecastCalculator.BusinessDays(worked, userNonWorking), adjust, pool, today);
            var competing = candidates.Count(c => new DateOnly(c.Payment.Year, c.Payment.Month, 1) == paymentMonth) > 1 || occurrences.Any(o => o.Overdue);
            occurrences.Add(competing && pool.Linked > 0m ? occurrence with { Ambiguous = true } : occurrence);
            pools[paymentMonth] = new(Math.Max(0m, pool.Linked - occurrence.GrossAmount), 0m);
        }
        return new(settings, occurrences, await TaxObligationMonthsAsync(db, workspaceId, settings.AccountId, new DateOnly(from.Year, from.Month, 1), through, ct), inferred is not null, inferred?.MonthsUsed ?? 0);
    }

    /// <summary>
    /// Sem configuração (PROJ-06): usa o faturamento profissional recente da conta PJ com mais recebimentos operacionais. Exclui rendimentos, estornos e devoluções (por descrição e
    /// origem) e entradas que espelham uma saída de mesmo valor em outra conta do próprio orçamento (transferência interna). Sem meses suficientes, não infere. Tributos não são
    /// inferidos (reserva 0%): ficam sinalizados como não configurados.
    /// </summary>
    private static async Task<(IncomeForecastSettings Settings, InferredIncome Inferred)?> InferAsync(Saldo360DbContext db, Guid workspaceId, DateOnly from, CancellationToken ct)
    {
        var currentMonth = new DateOnly(from.Year, from.Month, 1);
        var start = currentMonth.AddMonths(-IncomeInference.WindowMonths); var end = currentMonth.AddDays(-1);
        var pjAccounts = await db.FinancialAccounts.AsNoTracking().Where(a => a.WorkspaceId == workspaceId && a.IsActive && a.Scope == Saldo360.Domain.Accounts.FinancialScope.PJ).Select(a => a.Id).ToListAsync(ct);
        if (pjAccounts.Count == 0) return null;
        var incomes = await db.FinancialTransactions.AsNoTracking()
            .Where(t => t.WorkspaceId == workspaceId && !t.IsCancelled && t.Type == CategoryType.Income && t.Origin != Saldo360.Domain.Transactions.FinancialTransactionOrigin.PluggyRefund
                && pjAccounts.Contains(t.AccountId) && t.TransactionDate >= start && t.TransactionDate <= end)
            .Select(t => new { t.AccountId, t.TransactionDate, t.Amount, t.Description }).ToListAsync(ct);
        if (incomes.Count == 0) return null;
        var outgoing = await db.FinancialTransactions.AsNoTracking()
            .Where(t => t.WorkspaceId == workspaceId && !t.IsCancelled && t.Type == CategoryType.Expense && t.TransactionDate >= start.AddDays(-3) && t.TransactionDate <= end.AddDays(3))
            .Select(t => new { t.AccountId, t.TransactionDate, t.Amount }).ToListAsync(ct);
        var operational = incomes.Where(i => !outgoing.Any(o => o.AccountId != i.AccountId && o.Amount == i.Amount && Math.Abs(o.TransactionDate.DayNumber - i.TransactionDate.DayNumber) <= 3)).ToList();
        var account = operational.Where(i => IncomeInference.IsOperational(i.Description)).GroupBy(i => i.AccountId).OrderByDescending(g => g.Sum(i => i.Amount)).Select(g => g.Key).Cast<Guid?>().FirstOrDefault();
        if (account is null) return null;
        var inferred = IncomeInference.Infer(operational.Where(i => i.AccountId == account).Select(i => new OperationalReceipt(i.TransactionDate, i.Amount, i.Description)), currentMonth);
        return inferred is null ? null : (new IncomeForecastSettings(account.Value, 0m, 0m, inferred.PaymentDay, 0, 0m, 0m, true), inferred);
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
