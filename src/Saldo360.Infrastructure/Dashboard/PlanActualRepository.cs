using Microsoft.EntityFrameworkCore;
using Saldo360.Application.Budgets;
using Saldo360.Application.Dashboard.PlanActual;
using Saldo360.Domain.Budgets;
using Saldo360.Domain.Categories;
using Saldo360.Domain.Pluggy;
using Saldo360.Infrastructure.Persistence;

namespace Saldo360.Infrastructure.Dashboard;

/// <summary>
/// Fatos próprios do Dashboard Planejado × Projetado × Realizado (T10.1): previsões do período, movimentações PENDING da Pluggy, metas de cartões,
/// contagem de revisões e conexões por escopo. Somente leitura, restrito ao orçamento. Saldos, obrigações de cartão e conciliação vêm dos serviços existentes.
/// </summary>
internal sealed class PlanActualRepository(Saldo360DbContext db) : IPlanActualRepository
{
    /// <summary>
    /// A base de planejado/projetado de uma competência: previsões do período, movimentações PENDING da Pluggy e metas agregadas. É a mesma para o painel e para a
    /// visão de seis meses (T10.2), de modo que ambos usem as mesmas regras.
    /// </summary>
    public async Task<PlanBasisFacts> LoadBasisAsync(Guid workspaceId, DateOnly month, IReadOnlyList<Guid>? scopeAccountIds, CancellationToken ct)
    {
        var start = month; var end = month.AddMonths(1).AddDays(-1);
        var scope = scopeAccountIds?.ToList();

        // Planejado original: as previsões do período, ao valor previsto (a realização não altera o valor previsto). As que foram substituídas por uma fatura
        // oficial ou por uma parcela de dívida saem: a obrigação oficial já está nos cartões e nas dívidas.
        var planned = db.FinancialPlannedTransactions.AsNoTracking()
            .Where(p => p.WorkspaceId == workspaceId && !p.IsCancelled && p.DueDate >= start && p.DueDate <= end && (scope == null || scope.Contains(p.AccountId))
                && !db.FinancialPlannedInvoiceReconciliations.Any(r => r.WorkspaceId == p.WorkspaceId && r.PlannedTransactionId == p.Id && r.RemovedAt == null)
                && !db.FinancialDebtInstallmentReconciliations.Any(r => r.WorkspaceId == p.WorkspaceId && r.PlannedTransactionId == p.Id && r.RemovedAt == null));
        var plannedRows = await planned.GroupBy(p => p.Type).Select(g => new { Type = g.Key, Total = g.Sum(p => p.Amount), Count = g.Count() }).ToListAsync(ct);
        // Ocorrências virtuais de recorrência entram no planejado ao valor previsto (a exceção de competência já substituiu a recorrência; pagas ou não, o previsto é o original).
        var virtualPlan = (await Saldo360.Infrastructure.Recurrences.FixedObligationReader.LoadAsync(db, workspaceId, start, end, start, ct))
            .Where(o => o.IsVirtual && (scope == null || scope.Contains(o.AccountId))).ToList();
        plannedRows = plannedRows.Concat(virtualPlan.GroupBy(o => o.Type).Select(g => new { Type = g.Key, Total = g.Sum(o => o.PlannedAmount), Count = g.Count() })).GroupBy(r => r.Type)
            .Select(g => new { Type = g.Key, Total = g.Sum(r => r.Total), Count = g.Sum(r => r.Count) }).ToList();
        var income = plannedRows.FirstOrDefault(r => r.Type == CategoryType.Income); var expense = plannedRows.FirstOrDefault(r => r.Type == CategoryType.Expense);

        // T12.1: as metas efetivas do mês (meta de todos os cartões e total de despesas) vêm do resolvedor ÚNICO — exceção da competência, padrão vigente ou registro mensal histórico —, o mesmo
        // que alimenta a página Orçamento, a Visão Geral e a projeção. Nada é gravado por esta leitura.
        var effective = EffectiveBudgetResolver.Resolve(workspaceId, await Saldo360.Infrastructure.Budgets.BudgetLayerReader.ReadAsync(db, workspaceId, start, start.AddMonths(1), ct), start);
        var cardTarget = effective.CardTarget; var totalExpenses = effective.TotalExpenses;
        return new(new(income?.Total ?? 0m, income?.Count ?? 0, expense?.Total ?? 0m, expense?.Count ?? 0), cardTarget, totalExpenses);
    }

    public async Task<PlanActualFacts> LoadAsync(Guid workspaceId, DateOnly month, IReadOnlyList<Guid>? scopeAccountIds, CancellationToken ct)
    {
        var start = month; var end = month.AddMonths(1).AddDays(-1);
        var startUtc = new DateTimeOffset(start.ToDateTime(TimeOnly.MinValue), TimeSpan.Zero); var endUtc = startUtc.AddMonths(1);
        var basis = await LoadBasisAsync(workspaceId, month, scopeAccountIds, ct);

        // Revisões: o mesmo critério da conciliação de saldo (T09.5) — pendente de decisão ou ignorada depois de entrar no histórico.
        var review = await (from p in db.PluggyPostings.AsNoTracking()
                            join t in db.PluggyImportedTransactions.AsNoTracking() on p.ImportedTransactionId equals t.Id
                            join l in db.PluggyAccountLinks.AsNoTracking() on p.AccountLinkId equals l.Id
                            where p.WorkspaceId == workspaceId && t.Status == PluggyTransactionStatus.Posted
                               && (p.State == PluggyPostingState.NeedsReview || (p.State == PluggyPostingState.Ignored && p.Reason != "BeforeImportFrom"))
                            select new { l.Kind, l.FinancialAccountId, l.CreditCardId, t.Date }).ToListAsync(ct);
        var cardPayers = await db.FinancialCreditCards.AsNoTracking().Where(c => c.WorkspaceId == workspaceId && c.DefaultPaymentAccountId != null)
            .ToDictionaryAsync(c => c.Id, c => c.DefaultPaymentAccountId!.Value, ct);
        bool InMonth(DateTimeOffset? date) => date is { } d && d >= startUtc && d < endUtc;
        var bankReview = review.Where(r => r.Kind == PluggyLinkedAccountKind.Bank && r.FinancialAccountId != null).ToList();
        var reviewFacts = new ReviewFacts(
            bankReview.GroupBy(r => r.FinancialAccountId!.Value).ToDictionary(g => g.Key, g => g.Count()),
            bankReview.Where(r => InMonth(r.Date)).GroupBy(r => r.FinancialAccountId!.Value).ToDictionary(g => g.Key, g => g.Count()),
            review.Where(r => r.Kind == PluggyLinkedAccountKind.Card && r.CreditCardId != null && cardPayers.ContainsKey(r.CreditCardId.Value))
                .GroupBy(r => cardPayers[r.CreditCardId!.Value]).ToDictionary(g => g.Key, g => g.Count()));

        var links = await db.PluggyAccountLinks.AsNoTracking().Where(l => l.WorkspaceId == workspaceId).Select(l => new { l.ConnectionId, l.FinancialAccountId, l.CreditCardId }).ToListAsync(ct);
        var connections = links.Select(l => new ConnectionLink(l.ConnectionId, l.FinancialAccountId, l.CreditCardId is { } card && cardPayers.TryGetValue(card, out var payer) ? payer : null)).ToList();

        // Contas inativas só aparecem para escolha quando têm movimentação na competência (histórico aplicável).
        var activeIds = await db.FinancialTransactions.AsNoTracking().Where(t => t.WorkspaceId == workspaceId && !t.IsCancelled && t.TransactionDate >= start && t.TransactionDate <= end)
            .Select(t => t.AccountId).Distinct().ToListAsync(ct);
        var transferIds = await db.FinancialTransfers.AsNoTracking().Where(t => t.WorkspaceId == workspaceId && !t.IsCancelled && t.TransferDate >= start && t.TransferDate <= end)
            .Select(t => t.SourceAccountId).Concat(db.FinancialTransfers.AsNoTracking().Where(t => t.WorkspaceId == workspaceId && !t.IsCancelled && t.TransferDate >= start && t.TransferDate <= end).Select(t => t.DestinationAccountId))
            .Distinct().ToListAsync(ct);

        return new(basis.Planned, basis.CardBudgetTarget, basis.AggregateTotalExpenses,
            reviewFacts, connections, activeIds.Concat(transferIds).ToHashSet());
    }

    public async Task<DataFingerprint> FingerprintAsync(Guid workspaceId, CancellationToken ct)
    {
        var transactions = db.FinancialTransactions.AsNoTracking().Where(t => t.WorkspaceId == workspaceId && !t.IsCancelled);
        var plannedItems = db.FinancialPlannedTransactions.AsNoTracking().Where(p => p.WorkspaceId == workspaceId && !p.IsCancelled);
        return new(await transactions.LongCountAsync(ct), await transactions.SumAsync(t => (decimal?)t.Amount, ct) ?? 0m,
            await plannedItems.LongCountAsync(ct), await plannedItems.SumAsync(p => (decimal?)p.Amount, ct) ?? 0m,
            await db.PluggyImportedTransactions.AsNoTracking().LongCountAsync(t => t.WorkspaceId == workspaceId, ct),
            await db.FinancialCreditCardPurchases.AsNoTracking().LongCountAsync(p => p.WorkspaceId == workspaceId, ct),
            await db.FinancialAccountBalanceAnchors.AsNoTracking().LongCountAsync(a => a.WorkspaceId == workspaceId, ct),
            await db.PluggySyncRuns.AsNoTracking().Where(r => r.WorkspaceId == workspaceId).MaxAsync(r => (DateTimeOffset?)r.FinishedAt, ct),
            await db.FinancialPlanLines.AsNoTracking().LongCountAsync(l => l.WorkspaceId == workspaceId, ct),
            await db.FinancialPlanLines.AsNoTracking().Where(l => l.WorkspaceId == workspaceId && !l.IsArchived).SumAsync(l => (decimal?)l.Amount, ct) ?? 0m,
            await db.FinancialPlanLines.AsNoTracking().Where(l => l.WorkspaceId == workspaceId).MaxAsync(l => (DateTimeOffset?)l.UpdatedAt, ct));
    }

}
