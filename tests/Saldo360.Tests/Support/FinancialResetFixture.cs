using Microsoft.EntityFrameworkCore;
using Saldo360.Domain.Workspaces;
using Saldo360.Domain.Accounts;
using Saldo360.Domain.Budgets;
using Saldo360.Domain.Categories;
using Saldo360.Domain.CreditCards;
using Saldo360.Domain.Debts;
using Saldo360.Domain.Goals;
using Saldo360.Domain.Planning;
using Saldo360.Domain.Pluggy;
using Saldo360.Domain.Recurrences;
using Saldo360.Domain.ServiceAccounts;
using Saldo360.Domain.Transactions;
using Saldo360.Domain.Transfers;
using Saldo360.Infrastructure.Persistence;

namespace Saldo360.Tests.Support;

internal static class FinancialResetFixture
{
    public static async Task<string> FingerprintAsync(Saldo360DbContext db, Guid workspaceId)
    {
        var tables = db.Model.GetEntityTypes().Where(e => e.FindProperty("WorkspaceId") is not null
            && e.ClrType != typeof(FinancialResetAudit) && e.ClrType != typeof(FinancialResetFileCleanup))
            .Select(e => e.GetTableName()!).Order();
        var values = new List<string>();
        foreach (var table in tables)
        {
            // Identifiers are trusted EF metadata; the workspace remains parameterized.
            var sql = $"SELECT COALESCE(jsonb_agg(to_jsonb(t) ORDER BY id),'[]'::jsonb)::text AS \"Value\" FROM \"{table}\" t WHERE workspace_id={{0}}";
            values.Add(await db.Database.SqlQueryRaw<string>(sql, workspaceId).SingleAsync());
        }
        return string.Join("\n", values);
    }

    public static async Task<(Guid Account, Guid Category, Guid Planned)> SeedAsync(Saldo360DbContext db, Guid w, Guid u)
    {
        var now = DateTimeOffset.UtcNow; var date = new DateOnly(2026, 10, 1);
        FinancialAccount Account(string name) => FinancialAccount.Create(w, AccountDetails.TryCreate(name, null, "Checking", "PF", u, 1000m, date, out _)!, u, now);
        var account = Account("Account"); var destination = Account("Destination");
        var category = FinancialCategory.Create(w, "Expense", CategoryType.Expense, u, now);
        FinancialPlannedTransaction Planned(string description) => FinancialPlannedTransaction.Create(w, PlannedTransactionDetails.TryCreate(account.Id, category.Id, "Expense", description, 10m, date, out _)!, u, now);
        var realized = Planned("Realized"); var recurring = Planned("Recurring"); var invoicePlanned = Planned("Invoice"); var debtPlanned = Planned("Debt");
        var transaction = FinancialTransaction.CreateRealization(w, TransactionDetails.TryCreate(account.Id, category.Id, "Expense", "Realized", 10m, date, date, out _)!, realized.Id, u, now);
        var transfer = FinancialTransfer.Create(w, TransferDetails.TryCreate(account.Id, destination.Id, 1m, date, "Transfer", date, out _)!, u, now);
        var recurrence = FinancialRecurrence.Create(w, new(account.Id, category.Id, CategoryType.Expense, "Recurrence", 10m, false, RecurrenceFrequency.Monthly, 1, date, null, 1, 1), now);
        var materialization = FinancialRecurrenceMaterialization.Create(w, recurrence.Id, 1, date, recurring.Id, now);
        var budget = FinancialBudget.Create(w, date, null, u, now);
        var target = FinancialBudgetTarget.Create(w, budget.Id, "Target", 100m, category.Id, u, now);
        var aggregateTarget = FinancialMonthlyAggregateTarget.Create(w, date, MonthlyAggregateTargetType.TotalExpenses, 100m, now);
        var provenance = Saldo360.Domain.Bootstrap.FinancialBootstrapProvenance.Create(w, "SyntheticFixture", "Synthetic", "aggregate-target", new string('e', 64), nameof(FinancialMonthlyAggregateTarget), aggregateTarget.Id, null, now);
        var card = FinancialCreditCard.Create(w, new("Card", "Issuer", "1234", u, 1000m, 10, 20, account.Id), u, now);
        var invoice = FinancialCreditCardInvoice.Create(w, card.Id, "2026-10", date, date.AddDays(9), date.AddDays(19), now);
        var purchase = FinancialCreditCardPurchase.Create(w, card.Id, category.Id, "Purchase", date, 100m, 1, u, now);
        purchase.Installments.Single().AssignInvoice(invoice.Id);
        var cardTx = FinancialTransaction.CreateInvoicePayment(w, account.Id, "Card payment", 10m, date, u, now);
        var payment = FinancialCreditCardInvoicePayment.Create(w, invoice.Id, account.Id, cardTx.Id, Guid.NewGuid(), date, 10m, u, now);
        var adjustment = FinancialCreditCardInvoiceAdjustment.Create(w, card.Id, invoice.Id, FinancialCreditCardInvoiceAdjustmentType.Fee, FinancialCreditCardInvoiceAdjustmentNature.Debit, date, "Fee", 1m, "fee-1", category.Id, u, now);
        var batch = FinancialCreditCardImportBatch.Create(Guid.NewGuid(), w, card.Id, new string('a',64), u, now);
        batch.AddItem(FinancialCreditCardImportItem.Create(w, card.Id, batch.Id, 1, "external", purchase.Id, now));
        var imported = FinancialCreditCardImportedInstallment.Create(w, card.Id, batch.Id, 1, "imported", purchase.Installments.Single().Id, invoice.Id, date, "Imported", 100m, 1, 1, category.Id, false, u, now);
        var reconciliation = FinancialPlannedInvoiceReconciliation.Create(w, invoicePlanned.Id, invoice.Id, u, now);
        var debt = FinancialDebt.Create(w, new(account.Id, "Debt", null, DebtType.Other, null, 100m, null, null, null, date, date, 1, 0, 100m, null, null, null), now);
        var installment = FinancialDebtInstallment.Create(w, debt.Id, new(1, date, 100m, null, null, null, null, category.Id), now);
        var debtTx = FinancialTransaction.CreateDebtInstallmentPayment(w, account.Id, "Debt payment", 10m, date, u, now);
        var debtPayment = FinancialDebtPayment.Create(w, debt.Id, installment.Id, account.Id, new(Guid.NewGuid().ToString(), date, 10m, null, null, null, null), debtTx.Id, now);
        var debtReconciliation = FinancialDebtInstallmentReconciliation.Create(w, installment.Id, debtPlanned.Id, u, now);
        var goal = FinancialGoal.Create(w, new("Goal", null, FinancialGoalType.Other, 100m, null, FinancialGoalPriority.Medium), now);
        var allocation = FinancialGoalAllocation.Create(w, goal.Id, account.Id, FinancialGoalAllocationType.Contribution, 1m, date, Guid.NewGuid(), null, now);
        var scenario = FinancialPlanningScenario.Create(w, "Scenario", null, date, 6, now, account.Id);
        var parameter = FinancialPlanningMonthlyParameter.Create(w, scenario.Id, date, 100m, 10m, 1m, 1m, null);
        var documentId = Guid.NewGuid();
        var document = FinancialDocumentInboxItem.Create(documentId, w, FinancialDocumentSource.Email, "synthetic-email", "synthetic.pdf", new string('b',64), "application/pdf", $"workspaces/{w:N}/{documentId:N}.pdf", now, now);
        var binding = ServiceAccountBinding.Create(w, recurrence.Id, "ElectricityBill", "SYNTHETIC", new string('c',64), "***0000", null, null, now);
        var checkpoint = FinancialEmailCollectorCheckpoint.Create(w, "Synthetic");
        var service = ServiceAccount.Create(u, ServiceType.Electricity, "Synthetic", "Service", now);
        var identifier = ServiceAccountIdentifier.Create(service, ServiceIdentifierType.UtilityCustomerNumber, [1,2,3], new string('d',64), "***0000", now);
        var pluggyLink = PluggyItemLink.Create(w, u, Guid.NewGuid().ToString("D"), "Synthetic", now);
        var pluggyRun = PluggySyncRun.Start(w, pluggyLink.Id, u, now);
        var pluggySnapshot = new PluggyTransactionSnapshot("tx-reset", "acc-reset", "BANK", "Conta ••••0000", now, -10m, "BRL", "Synthetic", "SYNTHETIC", "DEBIT",
            PluggyTransactionStatus.Pending, "PIX", null, null, null, null, null, null);
        var pluggyTransaction = PluggyImportedTransaction.Create(w, pluggyLink.Id, pluggySnapshot, pluggyRun.Id, now);
        var pluggyAccountLink = PluggyAccountLink.Create(w, pluggyLink.Id, "acc-reset", PluggyLinkedAccountKind.Bank, account.Id, null, "Synthetic", null, "BANK", "CHECKING_ACCOUNT", null, u, now);
        var pluggyPosting = PluggyPosting.Create(w, pluggyTransaction.Id, pluggyAccountLink.Id, now);
        var pluggyBill = PluggyCardBill.Create(w, pluggyAccountLink.Id, "bill-reset", date, date.AddDays(7), 10m, "BRL", now);
        var pluggyChange = PluggyTransactionChange.Create(pluggyTransaction, pluggyTransaction.Observe(pluggySnapshot with { Status = PluggyTransactionStatus.Posted }, pluggyRun.Id, now), pluggyRun.Id, now);
        db.AddRange(account, destination, category, realized, recurring, invoicePlanned, debtPlanned, transaction, transfer,
            recurrence, materialization, budget, target, aggregateTarget, provenance, card, invoice, purchase, adjustment, batch,
            imported, reconciliation, debt, installment, debtTx, debtPayment, debtReconciliation, goal, allocation,
            scenario, parameter, document, binding, checkpoint, service, identifier, pluggyLink, pluggyRun, pluggyTransaction, pluggyChange, pluggyAccountLink, pluggyPosting, pluggyBill);
        await db.SaveChangesAsync();
        await OfficialInvoiceFixture.CloseAsync(db, invoice.Id);
        db.AddRange(cardTx, payment); await db.SaveChangesAsync();
        return (account.Id, category.Id, recurring.Id);
    }
}
