using Microsoft.EntityFrameworkCore;
using Saldo360.Application.Budgets;
using Saldo360.Domain.Categories;
using Saldo360.Domain.CreditCards;
using Saldo360.Domain.Transactions;
using Saldo360.Infrastructure.Persistence;

namespace Saldo360.Infrastructure.Budgets;

/// <summary>
/// Fonte única dos registros que consomem orçamento (R03). Lê UMA vez, por intervalo de datas, todas as despesas do orçamento da família; cada meta
/// (categoria, fixas, cartões) é só um filtro sobre esta lista (<see cref="BudgetTargetMatcher"/>), então resumo, composição, painel e projeções
/// nunca divergem. Qual registro vale por obrigação:
/// <list type="bullet">
/// <item>despesa efetiva = lançamento de despesa (data efetiva), sem pagamento de fatura nem de parcela de dívida;</item>
/// <item>compra no cartão = cada parcela, na competência da fatura (mês do vencimento); o pagamento da fatura não conta de novo;</item>
/// <item>encargo conta uma vez; crédito/estorno de fatura reduz; saldo anterior e financiado não são consumo;</item>
/// <item>dívida = pagamentos efetivos (data do pagamento) + saldo ainda não pago da parcela (vencimento); o lançamento bancário do pagamento não conta;</item>
/// <item>previsão pendente = previsto adicional; realizada ou conciliada com fatura/parcela deixa de contar como pendente.</item>
/// </list>
/// </summary>
internal static class BudgetFactReader
{
    public static async Task<IReadOnlyList<BudgetFact>> ReadAsync(Saldo360DbContext db, Guid workspaceId, DateOnly first, DateOnly through, CancellationToken ct)
    {
        var facts = new List<BudgetFact>();

        // 1. Despesas efetivas.
        var transactions = await (from t in db.FinancialTransactions.AsNoTracking()
                                  join a in db.FinancialAccounts.AsNoTracking() on t.AccountId equals a.Id
                                  where t.WorkspaceId == workspaceId && t.Type == CategoryType.Expense && !t.IsCancelled
                                      && t.TransactionDate >= first && t.TransactionDate < through
                                      && t.Origin != FinancialTransactionOrigin.CreditCardInvoicePayment && t.Origin != FinancialTransactionOrigin.DebtInstallmentPayment
                                  select new { t.Id, t.TransactionDate, t.Amount, t.CategoryId, t.Nature, t.Description, t.AccountId, AccountName = a.Name, Imported = t.Origin == FinancialTransactionOrigin.PluggyImport }).ToListAsync(ct);
        facts.AddRange(transactions.Select(t => new BudgetFact(BudgetFactOrigin.Transaction, t.Id, t.TransactionDate, t.Amount, BudgetFactState.Realized, t.CategoryId, t.Nature,
            t.Description, AccountId: t.AccountId, AccountName: t.AccountName, Imported: t.Imported)));

        // 2. Previsões pendentes (previsto adicional).
        var planned = await (from p in db.FinancialPlannedTransactions.AsNoTracking()
                             join a in db.FinancialAccounts.AsNoTracking() on p.AccountId equals a.Id
                             where p.WorkspaceId == workspaceId && p.Type == CategoryType.Expense && !p.IsCancelled && p.DueDate >= first && p.DueDate < through
                                 && !db.FinancialTransactions.Any(t => t.WorkspaceId == p.WorkspaceId && t.PlannedTransactionId == p.Id && !t.IsCancelled)
                                 && !db.FinancialPlannedInvoiceReconciliations.Any(r => r.WorkspaceId == p.WorkspaceId && r.PlannedTransactionId == p.Id && r.RemovedAt == null)
                                 && !db.FinancialDebtInstallmentReconciliations.Any(r => r.WorkspaceId == p.WorkspaceId && r.PlannedTransactionId == p.Id && r.RemovedAt == null)
                             select new { p.Id, p.DueDate, p.Amount, p.CategoryId, p.Nature, p.Description, p.AccountId, AccountName = a.Name }).ToListAsync(ct);
        facts.AddRange(planned.Select(p => new BudgetFact(BudgetFactOrigin.PlannedTransaction, p.Id, p.DueDate, p.Amount, BudgetFactState.Pending, p.CategoryId, p.Nature,
            p.Description, AccountId: p.AccountId, AccountName: p.AccountName)));

        // 3. Dívidas: pagamento efetivo (caixa) e saldo ainda não pago da parcela.
        var debtPayments = await (from payment in db.FinancialDebtPayments.AsNoTracking()
                                  join installment in db.FinancialDebtInstallments.AsNoTracking() on new { payment.WorkspaceId, InstallmentId = payment.InstallmentId } equals new { installment.WorkspaceId, InstallmentId = installment.Id }
                                  join debt in db.FinancialDebts.AsNoTracking() on new { installment.WorkspaceId, DebtId = installment.DebtId } equals new { debt.WorkspaceId, DebtId = debt.Id }
                                  where payment.WorkspaceId == workspaceId && payment.CancelledAt == null && payment.PaymentDate >= first && payment.PaymentDate < through
                                  select new { payment.Id, payment.PaymentDate, payment.Amount, installment.CategoryId, installment.Nature, DebtName = debt.Name, installment.InstallmentNumber, payment.FinancialAccountId }).ToListAsync(ct);
        facts.AddRange(debtPayments.Select(d => new BudgetFact(BudgetFactOrigin.DebtPayment, d.Id, d.PaymentDate, d.Amount, BudgetFactState.Realized, d.CategoryId, d.Nature,
            $"{d.DebtName} — parcela {d.InstallmentNumber} (pagamento)", AccountId: d.FinancialAccountId)));
        var debtRemaining = await (from installment in db.FinancialDebtInstallments.AsNoTracking()
                                   join debt in db.FinancialDebts.AsNoTracking() on new { installment.WorkspaceId, DebtId = installment.DebtId } equals new { debt.WorkspaceId, DebtId = debt.Id }
                                   where installment.WorkspaceId == workspaceId && installment.DueDate >= first && installment.DueDate < through && installment.CancelledAt == null
                                   let paid = db.FinancialDebtPayments.Where(payment => payment.WorkspaceId == workspaceId && payment.InstallmentId == installment.Id && payment.CancelledAt == null).Sum(payment => (decimal?)payment.Amount) ?? 0m
                                   where installment.ScheduledAmount > paid
                                   select new { installment.Id, installment.DueDate, Amount = installment.ScheduledAmount - paid, installment.CategoryId, installment.Nature, DebtName = debt.Name, installment.InstallmentNumber }).ToListAsync(ct);
        facts.AddRange(debtRemaining.Select(d => new BudgetFact(BudgetFactOrigin.DebtInstallment, d.Id, d.DueDate, d.Amount, BudgetFactState.Pending, d.CategoryId, d.Nature,
            $"{d.DebtName} — parcela {d.InstallmentNumber}")));

        // 4. Cartões: parcela por competência da fatura (vencimento).
        var cardRows = await (from installment in db.FinancialCreditCardInstallments.AsNoTracking()
                              join purchase in db.FinancialCreditCardPurchases.AsNoTracking() on installment.PurchaseId equals purchase.Id
                              join invoice in db.FinancialCreditCardInvoices.AsNoTracking() on installment.InvoiceId equals invoice.Id
                              join card in db.FinancialCreditCards.AsNoTracking() on purchase.CreditCardId equals card.Id
                              where installment.WorkspaceId == workspaceId && installment.Status == CreditCardInstallmentStatus.Active
                                  && purchase.WorkspaceId == workspaceId && purchase.Status == CreditCardPurchaseStatus.Active
                                  && invoice.WorkspaceId == workspaceId && invoice.DueDate >= first && invoice.DueDate < through
                              select new
                              {
                                  installment.Id, PurchaseId = purchase.Id, invoice.DueDate, installment.Amount, CategoryId = installment.OfficialCategoryId ?? purchase.CategoryId,
                                  purchase.Nature, purchase.Description, installment.InstallmentNumber, purchase.InstallmentCount,
                                  CardId = card.Id, CardName = card.Name, invoice.Competence, Closed = invoice.InstitutionClosedAt != null || invoice.OfficialVersion > 0,
                              }).ToListAsync(ct);
        // Compra importada do banco só tem competência confiável se veio com a fatura (bill) do banco ou se a fatura foi fechada pela instituição;
        // sem isso a competência vem apenas do calendário configurado do cartão e é tratada como estimativa (nunca "mês corrente" em silêncio).
        var purchaseIds = cardRows.Select(c => c.PurchaseId).Distinct().ToList();
        var imported = purchaseIds.Count == 0 ? [] : await (from p in db.PluggyPostings.AsNoTracking()
                                                            join i in db.PluggyImportedTransactions.AsNoTracking() on p.ImportedTransactionId equals i.Id
                                                            where p.WorkspaceId == workspaceId && p.CreditCardPurchaseId != null && purchaseIds.Contains(p.CreditCardPurchaseId.Value)
                                                            select new { PurchaseId = p.CreditCardPurchaseId!.Value, HasBill = i.BillId != null }).ToListAsync(ct);
        var hasBill = imported.GroupBy(i => i.PurchaseId).ToDictionary(g => g.Key, g => g.Any(i => i.HasBill));
        facts.AddRange(cardRows.Select(c => new BudgetFact(BudgetFactOrigin.CardInstallment, c.Id, c.DueDate, c.Amount, BudgetFactState.CardLaunched, c.CategoryId, c.Nature,
            c.InstallmentCount > 1 ? $"{c.Description} ({c.InstallmentNumber}/{c.InstallmentCount})" : c.Description, c.CardId, c.CardName, InvoiceCompetence: c.Competence,
            CompetenceReliable: c.Closed || !hasBill.TryGetValue(c.PurchaseId, out var bill) || bill, PurchaseId: c.PurchaseId, Imported: hasBill.ContainsKey(c.PurchaseId))));

        var knownRows = await (from item in db.FinancialCreditCardImportedInstallments.AsNoTracking()
                               join card in db.FinancialCreditCards.AsNoTracking() on item.CreditCardId equals card.Id
                               join invoice in db.FinancialCreditCardInvoices.AsNoTracking() on item.InvoiceId equals invoice.Id into invoices
                               from invoice in invoices.DefaultIfEmpty()
                               where item.WorkspaceId == workspaceId && item.IsHistorical && item.Status == FinancialCreditCardImportedInstallmentStatus.Active
                                   && (invoice != null ? invoice.DueDate : item.DueDate) >= first && (invoice != null ? invoice.DueDate : item.DueDate) < through
                               select new { item.Id, Due = invoice != null ? invoice.DueDate : item.DueDate!.Value, item.Amount, item.CategoryId, item.Description, CardId = card.Id, CardName = card.Name,
                                   Competence = invoice != null ? invoice.Competence : null }).ToListAsync(ct);
        facts.AddRange(knownRows.Select(k => new BudgetFact(BudgetFactOrigin.CardKnownInstallment, k.Id, k.Due, k.Amount, BudgetFactState.CardLaunched, k.CategoryId, ExpenseNature.Undefined,
            k.Description, k.CardId, k.CardName, InvoiceCompetence: k.Competence)));

        var adjustments = await (from adjustment in db.FinancialCreditCardInvoiceAdjustments.AsNoTracking()
                                 join invoice in db.FinancialCreditCardInvoices.AsNoTracking() on adjustment.InvoiceId equals invoice.Id
                                 join card in db.FinancialCreditCards.AsNoTracking() on adjustment.CreditCardId equals card.Id
                                 where adjustment.WorkspaceId == workspaceId && invoice.WorkspaceId == workspaceId && adjustment.Status == FinancialCreditCardInvoiceAdjustmentStatus.Active
                                     && (adjustment.Type == FinancialCreditCardInvoiceAdjustmentType.Fee || adjustment.Type == FinancialCreditCardInvoiceAdjustmentType.Credit
                                         || adjustment.Type == FinancialCreditCardInvoiceAdjustmentType.Refund)
                                     && invoice.DueDate >= first && invoice.DueDate < through
                                 select new
                                 {
                                     adjustment.Id, invoice.DueDate, adjustment.Amount, adjustment.Nature, adjustment.CategoryId, adjustment.Description, adjustment.Type,
                                     CardId = card.Id, CardName = card.Name, invoice.Competence,
                                 }).ToListAsync(ct);
        facts.AddRange(adjustments.Select(a => new BudgetFact(BudgetFactOrigin.CardAdjustment, a.Id, a.DueDate,
            a.Nature == FinancialCreditCardInvoiceAdjustmentNature.Debit ? a.Amount : -a.Amount, BudgetFactState.CardLaunched, a.CategoryId, ExpenseNature.Undefined,
            a.Description, a.CardId, a.CardName, InvoiceCompetence: a.Competence)));

        return facts;
    }

    /// <summary>Subcategoria → principal, para as categorias-alvo (uma meta de principal cobre as subcategorias).</summary>
    public static async Task<Dictionary<Guid, Guid>> ParentsAsync(Saldo360DbContext db, Guid workspaceId, IReadOnlyCollection<Guid> targetCategoryIds, CancellationToken ct) =>
        targetCategoryIds.Count == 0 ? [] : (await db.FinancialCategories.AsNoTracking()
            .Where(c => c.WorkspaceId == workspaceId && c.ParentCategoryId != null && targetCategoryIds.Contains(c.ParentCategoryId.Value))
            .Select(c => new { c.Id, Parent = c.ParentCategoryId!.Value }).ToListAsync(ct)).ToDictionary(c => c.Id, c => c.Parent);
}
