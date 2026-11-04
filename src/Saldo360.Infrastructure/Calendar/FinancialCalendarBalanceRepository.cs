using System.Data;
using Microsoft.EntityFrameworkCore;
using Saldo360.Application.Calendar;
using Saldo360.Domain.Categories;
using Saldo360.Infrastructure.Recurrences;
using Saldo360.Infrastructure.Persistence;

namespace Saldo360.Infrastructure.Calendar;

internal sealed class FinancialCalendarBalanceRepository(Saldo360DbContext db)
    : IFinancialCalendarBalanceRepository
{
    public Task<FinancialCalendarBalanceSnapshot?> ReadAsync(
        Guid workspaceId,
        Guid? accountId,
        DateOnly through,
        DateOnly today,
        CancellationToken cancellationToken) =>
        ReadWithPlanAsync(workspaceId, accountId, null, through, today, cancellationToken);

    public Task<FinancialCalendarBalanceSnapshot?> ReadMonthlyAsync(
        Guid workspaceId,
        Guid? accountId,
        DateOnly from,
        DateOnly through,
        DateOnly today,
        CancellationToken cancellationToken) =>
        ReadWithPlanAsync(workspaceId, accountId, from, through, today, cancellationToken);

    /// <summary>Leitura da PJ para o planejamento do repasse: o mesmo saldo, SEM o repasse projetado (evita recursão e dupla contagem).</summary>
    public Task<FinancialCalendarBalanceSnapshot?> ReadWithoutAutoTransfersAsync(Guid workspaceId, Guid? accountId, DateOnly through, DateOnly today, CancellationToken cancellationToken) =>
        ReadCoreAsync(workspaceId, accountId, null, through, today, null, cancellationToken);

    private async Task<FinancialCalendarBalanceSnapshot?> ReadWithPlanAsync(Guid workspaceId, Guid? accountId, DateOnly? periodFrom, DateOnly through, DateOnly today, CancellationToken cancellationToken) =>
        await ReadCoreAsync(workspaceId, accountId, periodFrom, through, today, await PjTransferProjection.PlanAsync(db, workspaceId, today, cancellationToken), cancellationToken);

    private async Task<FinancialCalendarBalanceSnapshot?> ReadCoreAsync(
        Guid workspaceId,
        Guid? accountId,
        DateOnly? periodFrom,
        DateOnly through,
        DateOnly today,
        AutoTransferPlan? autoPlan,
        CancellationToken cancellationToken)
    {
        await using var transaction = await db.Database.BeginTransactionAsync(
            IsolationLevel.RepeatableRead, cancellationToken);
        await db.Database.ExecuteSqlRawAsync("SET TRANSACTION READ ONLY", cancellationToken);

        // Saldo inicial efetivo: o manual (quando informado) ou, sem ele, o derivado da referência bancária confiável (âncora). Nada é gravado na conta.
        var selectedAccounts =
            from account in db.FinancialAccounts.AsNoTracking()
            where account.WorkspaceId == workspaceId && (accountId == null || account.Id == accountId)
            join anchor in db.FinancialAccountBalanceAnchors.AsNoTracking().Where(a => a.IsCurrent && a.Reliability != Saldo360.Domain.Accounts.BalanceAnchorReliability.Unreliable
                && a.DerivedOpeningAmount != null && a.DerivedOpeningDate != null) on account.Id equals anchor.AccountId into anchors
            from anchor in anchors.DefaultIfEmpty()
            let manual = account.OpeningBalance != null && account.OpeningBalanceDate != null
            select new
            {
                account.WorkspaceId,
                account.Id,
                OpeningBalance = manual ? account.OpeningBalance : anchor.DerivedOpeningAmount,
                OpeningBalanceDate = manual ? account.OpeningBalanceDate : anchor.DerivedOpeningDate,
            };
        var accounts = await selectedAccounts
            .Select(account => new FinancialCalendarBalanceAccount(
                account.Id, account.OpeningBalance, account.OpeningBalanceDate))
            .ToListAsync(cancellationToken);
        if (accountId is not null && accounts.Count == 0)
        {
            await transaction.CommitAsync(cancellationToken);
            return null;
        }

        var lastRealizedDate = through < today ? through : today;
        // Receita e despesa pela regra única dos indicadores (devolução bancária: entrada de caixa que reduz a despesa, nunca receita). O saldo não muda:
        // Receita − Despesa líquida = Entradas − Saídas.
        var transactionRows = await (
                from movement in Saldo360.Infrastructure.Transactions.FinancialTransactionKpi.Project(
                    from t in db.FinancialTransactions.AsNoTracking()
                    join account in selectedAccounts on new { t.WorkspaceId, Id = t.AccountId } equals new { account.WorkspaceId, account.Id }
                    where !t.IsCancelled && account.OpeningBalanceDate != null && t.TransactionDate >= account.OpeningBalanceDate.Value && t.TransactionDate <= lastRealizedDate
                    select t)
                group movement by movement.Date into daily
                select new
                {
                    Date = daily.Key,
                    Income = daily.Sum(item => item.Revenue),
                    Expense = daily.Sum(item => item.NetExpense),
                })
            .ToListAsync(cancellationToken);
        var transactionTotals = transactionRows
            .Select(row => new FinancialCalendarDailyTotals(row.Date, row.Income, row.Expense, 0m, 0m))
            .ToList();

        // Respect each account's own reference date, as T06.3 does. With different opening dates,
        // one side of an older transfer can already be included in its account's opening balance.
        var transfers = db.FinancialTransfers.AsNoTracking().Where(item => item.WorkspaceId == workspaceId
            && !item.IsCancelled && item.TransferDate <= lastRealizedDate);
        var incoming = from item in transfers
                       join account in selectedAccounts on new { item.WorkspaceId, Id = item.DestinationAccountId } equals new { account.WorkspaceId, account.Id }
                       where account.OpeningBalanceDate != null && item.TransferDate >= account.OpeningBalanceDate.Value
                       select new { Date = item.TransferDate, In = item.Amount, Out = 0m };
        var outgoing = from item in transfers
                       join account in selectedAccounts on new { item.WorkspaceId, Id = item.SourceAccountId } equals new { account.WorkspaceId, account.Id }
                       where account.OpeningBalanceDate != null && item.TransferDate >= account.OpeningBalanceDate.Value
                       select new { Date = item.TransferDate, In = 0m, Out = item.Amount };
        var transferRows = await incoming.Concat(outgoing).GroupBy(row => row.Date)
            .Select(daily => new { Date = daily.Key, In = daily.Sum(row => row.In), Out = daily.Sum(row => row.Out) })
            .ToListAsync(cancellationToken);
        var transferTotals = transferRows.Select(row => new FinancialCalendarDailyTotals(row.Date, 0m, 0m,
            accountId is null ? Math.Max(0m, row.In - row.Out) : row.In,
            accountId is null ? Math.Max(0m, row.Out - row.In) : row.Out)).ToList();

        var realized = transactionTotals
            .Concat(transferTotals)
            .GroupBy(row => row.Date)
            .Select(group => new FinancialCalendarDailyTotals(
                group.Key,
                group.Sum(row => row.Income),
                group.Sum(row => row.Expense),
                group.Sum(row => row.TransferIn),
                group.Sum(row => row.TransferOut)))
            .OrderBy(row => row.Date)
            .ToList();

        // Regra única de pendência (PlannedObligationQueries) + ocorrências virtuais de recorrência do mês corrente em diante.
        var pendingQuery = db.PendingPlanned(workspaceId).Where(item => item.DueDate <= through);
        if (accountId is { } pendingAccountId)
        {
            pendingQuery = pendingQuery.Where(item => item.AccountId == pendingAccountId);
        }

        var pendingRows = await pendingQuery
            .GroupBy(item => item.DueDate)
            .Select(daily => new
            {
                Date = daily.Key,
                Income = daily.Sum(item => item.Type == CategoryType.Income ? (item.ConfirmedAmount ?? item.Amount) : 0m),
                Expense = daily.Sum(item => item.Type == CategoryType.Expense ? (item.ConfirmedAmount ?? item.Amount) : 0m),
            })
            .OrderBy(row => row.Date)
            .ToListAsync(cancellationToken);
        var pending = pendingRows
            .Select(row => new FinancialCalendarDailyTotals(row.Date, row.Income, row.Expense, 0m, 0m))
            .ToList();
        var virtualPending = (await Saldo360.Infrastructure.Recurrences.FixedObligationReader.LoadVirtualPendingAsync(db, workspaceId,
                new DateOnly(today.Year, today.Month, 1), through, today, cancellationToken))
            .Where(o => accountId == null || o.AccountId == accountId).ToList();
        pending = pending.Concat(virtualPending.GroupBy(o => o.DueDate).Select(g => new FinancialCalendarDailyTotals(g.Key,
            g.Where(o => o.Type == CategoryType.Income).Sum(o => o.PlannedAmount), g.Where(o => o.Type == CategoryType.Expense).Sum(o => o.PlannedAmount), 0m, 0m))).ToList();

        // Receita PJ por horas (PROJ-01): estimativa parametrizada, só quando a configuração foi ATIVADA pelo usuário. Entra como entrada pendente na data do recebimento, na conta
        // de recebimento; a receita realizada da conta no mês a substitui (nunca as duas) e a reserva de tributos não é débito aqui.
        var forecast = await Saldo360.Infrastructure.MonthlyPlan.IncomeForecastLoader.LoadAsync(db, workspaceId, new DateOnly(today.Year, today.Month, 1), through, cancellationToken);
        if (forecast.Effective && (accountId == null || accountId == forecast.Settings!.AccountId))
            pending = pending.Concat(forecast.Occurrences.Where(o => o.ProjectsCash && o.GrossAmount > 0m).GroupBy(o => o.PaymentDate)
                .Select(g => new FinancialCalendarDailyTotals(g.Key, g.Sum(o => o.GrossAmount), 0m, 0m, 0m))).ToList();

        // Transferências PREVISTAS (pendentes: não canceladas e ainda sem transferência realizada associada): saída na origem e entrada no destino. Numa conta, só a ponta dela; no
        // consolidado, o líquido por dia (as duas pontas dentro do conjunto se anulam). Associada, a previsão deixa de projetar: o realizado já conta a transferência uma única vez.
        var plannedTransfers = await (from item in db.FinancialPlannedTransfers.AsNoTracking()
                                      where item.WorkspaceId == workspaceId && !item.IsCancelled && item.RealizedTransferId == null && item.DueDate <= through
                                      select new { item.DueDate, item.SourceAccountId, item.DestinationAccountId, item.Amount }).ToListAsync(cancellationToken);
        var selectedIds = await selectedAccounts.Select(account => account.Id).ToListAsync(cancellationToken);
        var plannedTransferRows = plannedTransfers
            .Select(item => new { item.DueDate, In = selectedIds.Contains(item.DestinationAccountId) ? item.Amount : 0m, Out = selectedIds.Contains(item.SourceAccountId) ? item.Amount : 0m })
            .GroupBy(item => item.DueDate)
            .Select(daily => new { Date = daily.Key, In = daily.Sum(item => item.In), Out = daily.Sum(item => item.Out) })
            .Where(row => row.In != 0m || row.Out != 0m)
            .Select(row => new FinancialCalendarDailyTotals(row.Date, 0m, 0m, accountId is null ? Math.Max(0m, row.In - row.Out) : row.In, accountId is null ? Math.Max(0m, row.Out - row.In) : row.Out))
            .ToList();
        pending = pending.Concat(plannedTransferRows).ToList();

        // Repasse PJ → PF PROJETADO (PROJ-06): saída na PJ e entrada na PF, só planejamento (nenhuma transferência real). Mesma regra das transferências previstas: numa conta, só a
        // ponta dela; no consolidado as duas pontas se anulam (neutro). Nunca é receita nem despesa.
        if (autoPlan is { Transfers.Count: > 0 })
            pending = pending.Concat(autoPlan.Transfers.GroupBy(item => item.Date)
                .Select(daily => new { Date = daily.Key, Amount = daily.Sum(item => item.Amount) })
                .Select(daily => new { daily.Date, In = selectedIds.Contains(autoPlan.PfAccountId) ? daily.Amount : 0m, Out = selectedIds.Contains(autoPlan.PjAccountId) ? daily.Amount : 0m })
                .Where(row => row.In != 0m || row.Out != 0m)
                .Select(row => new FinancialCalendarDailyTotals(row.Date, 0m, 0m, accountId is null ? Math.Max(0m, row.In - row.Out) : row.In, accountId is null ? Math.Max(0m, row.Out - row.In) : row.Out))).ToList();

        var selectedAccountIds = await selectedAccounts.Select(account => account.Id).ToListAsync(cancellationToken);
        var debtRows = await (from installment in db.FinancialDebtInstallments.AsNoTracking()
                              join debt in db.FinancialDebts.AsNoTracking() on new { installment.WorkspaceId, DebtId = installment.DebtId } equals new { debt.WorkspaceId, DebtId = debt.Id }
                              where installment.WorkspaceId == workspaceId && debt.Status != Saldo360.Domain.Debts.DebtStatus.Archived
                                  && installment.CancelledAt == null && installment.DueDate <= through
                                  && (accountId == null || debt.ResponsibleAccountId == accountId)
                              let paid = db.FinancialDebtPayments.Where(payment => payment.WorkspaceId == workspaceId && payment.InstallmentId == installment.Id && payment.CancelledAt == null).Sum(payment => (decimal?)payment.Amount) ?? 0m
                              let remaining = installment.ScheduledAmount - paid
                              where remaining > 0m
                              select new { installment.Id, installment.DueDate, debt.ResponsibleAccountId, remaining }).ToListAsync(cancellationToken);
        var debtPending = debtRows.GroupBy(row => row.DueDate).Select(group => new FinancialCalendarDailyTotals(group.Key, 0m, group.Sum(row => row.remaining), 0m, 0m)).ToList();
        var hasUnallocatedDebt = debtRows.Any(row => row.ResponsibleAccountId == null || !selectedAccountIds.Contains(row.ResponsibleAccountId.Value));

        // Obrigações dos cartões (T09.4): a mesma fonte do módulo de cartões. Fatura fechada pelo banco vale o total bancário; aberta e futura usam a
        // projeção (compras pendentes, parcelas devolvidas e estimadas); pagamentos reduzem uma única vez. Cada obrigação vai para a conta pagadora.
        var cardPending = new List<FinancialCalendarDailyTotals>();
        var cardCandidates = new List<PendingBankCandidate>();
        var allCardObligations = false;
        foreach (var item in await Saldo360.Infrastructure.CreditCards.CardProjectionRepository.ReadItemsAsync(db, workspaceId, today, cancellationToken))
        {
            if (item.DueDate is { } candidateDue && item.PayerAccountId is { } candidatePayer && item.Remaining > 0m) cardCandidates.Add(new(Guid.Empty, candidatePayer, item.Remaining, candidateDue, "CardInvoice"));
            if (item.DueDate is { } itemDue && itemDue > through) continue;
            var allocated = item.PayerAccountId is { } payer && selectedAccountIds.Contains(payer);
            if (!allocated || (item.Kind == Saldo360.Application.CreditCards.Projection.ObligationKind.KnownImported && item.DueDate is null)) allCardObligations = true;
            if (allocated && item.DueDate is { } due && item.Remaining > 0m) cardPending.Add(new FinancialCalendarDailyTotals(due, 0m, item.Remaining, 0m, 0m));
        }

        // PendingBank (PROJ-03): débito bancário pendente da Pluggy. Coberto pela agenda = nada a somar; sem candidata = entra uma única vez; ambíguo = fica fora do saldo e é sinalizado.
        var pendingDebits = await PendingBankReader.LoadDebitsAsync(db, workspaceId, selectedAccountIds, through, cancellationToken);
        IReadOnlyList<PendingBankClassification> pendingBank = [];
        if (pendingDebits.Count > 0)
        {
            var lower = pendingDebits.Min(d => d.Date).AddDays(-Saldo360.Application.Recurrences.FixedObligationMatcher.DaysAfter - 1);
            var upper = through.AddDays(Saldo360.Application.Recurrences.FixedObligationMatcher.DaysBefore);
            // Obrigações da agenda: recorrências/previsões fixas (leitor único) + previsões avulsas pendentes (mesma regra de pendência da agenda). Cada uma conta uma vez.
            var plainPlanned = await db.PendingPlanned(workspaceId).Where(p => p.Type == CategoryType.Expense && p.DueDate >= lower && p.DueDate <= upper && selectedAccountIds.Contains(p.AccountId)).ToListAsync(cancellationToken);
            var fixedObligations = (await FixedObligationReader.LoadAsync(db, workspaceId, lower, upper, today, cancellationToken))
                .Concat(plainPlanned.Select(p => new Saldo360.Application.Recurrences.FixedObligation(workspaceId, null, null, new(p.DueDate.Year, p.DueDate.Month, 1), p.Id, p.AccountId, p.CategoryId, p.Type, p.Nature,
                    p.Description, p.ConfirmedAmount ?? p.Amount, false, p.DueDate, Saldo360.Application.Recurrences.ObligationSituation.Planned, false)))
                .GroupBy(o => o.StableId).Select(g => g.First()).ToList();
            var otherCandidates = debtRows.Where(r => r.ResponsibleAccountId != null).Select(r => new PendingBankCandidate(r.Id, r.ResponsibleAccountId!.Value, r.remaining, r.DueDate, "DebtInstallment"))
                .Concat(plannedTransfers.Select(t => new PendingBankCandidate(Guid.Empty, t.SourceAccountId, t.Amount, t.DueDate, "PlannedTransfer")))
                .Concat(cardCandidates).ToList();
            pendingBank = PendingBankCoverage.Classify(pendingDebits, fixedObligations, otherCandidates);
            pending = pending.Concat(pendingBank.Where(c => c.State == PendingBankCoverageState.Uncovered).GroupBy(c => c.Debit.Date)
                .Select(g => new FinancialCalendarDailyTotals(g.Key, 0m, g.Sum(c => c.Debit.Amount), 0m, 0m))).ToList();
        }

        IReadOnlyList<FinancialCalendarDailyTotals> periodRealized = [];
        IReadOnlyList<FinancialCalendarDailyTotals> cardPayments = [];
        IReadOnlyList<Saldo360.Application.Budgets.FinancialBudgetExecutionSource> budgets = [];
        if (periodFrom is { } firstPeriodDate)
        {
            var periodRows = await (
                    from movement in Saldo360.Infrastructure.Transactions.FinancialTransactionKpi.Project(
                        from t in db.FinancialTransactions.AsNoTracking()
                        join account in selectedAccounts on new { t.WorkspaceId, Id = t.AccountId } equals new { account.WorkspaceId, account.Id }
                        where !t.IsCancelled && t.TransactionDate >= firstPeriodDate && t.TransactionDate <= through
                        select t)
                    group movement by movement.Date into daily
                    select new
                    {
                        Date = daily.Key,
                        Income = daily.Sum(item => item.Revenue),
                        Expense = daily.Sum(item => item.NetExpense),
                        CardPayments = daily.Sum(item => item.CardPayment),
                    })
                .OrderBy(row => row.Date)
                .ToListAsync(cancellationToken);
            periodRealized = periodRows
                .Select(row => new FinancialCalendarDailyTotals(row.Date, row.Income, row.Expense, 0m, 0m))
                .ToList();
            cardPayments = periodRows.Select(row => new FinancialCalendarDailyTotals(row.Date, 0m, row.CardPayments, 0m, 0m)).ToList();
            budgets = await Saldo360.Infrastructure.Budgets.FinancialBudgetExecutionRepository.ReadRangeAsync(
                db, workspaceId, firstPeriodDate, through.AddDays(1), null, cancellationToken, today);
        }

        await transaction.CommitAsync(cancellationToken);
        return new FinancialCalendarBalanceSnapshot(accounts, realized, pending, periodRealized, cardPending, allCardObligations, debtPending, hasUnallocatedDebt, cardPayments, budgets, pendingBank);
    }
}
