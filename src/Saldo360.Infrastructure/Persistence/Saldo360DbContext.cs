using Microsoft.AspNetCore.DataProtection.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore;
using Saldo360.Domain.Accounts;
using Saldo360.Domain.Budgets;
using Saldo360.Domain.Debts;
using Saldo360.Domain.Categories;
using Saldo360.Domain.CreditCards;
using Saldo360.Domain.Families;
using Saldo360.Domain.Planning;
using Saldo360.Domain.Recurrences;
using Saldo360.Domain.Transactions;
using Saldo360.Domain.Transfers;
using Saldo360.Domain.Users;
using Saldo360.Domain.Goals;
using Saldo360.Infrastructure.Authentication;
using Saldo360.Infrastructure.Persistence.Configurations;

namespace Saldo360.Infrastructure.Persistence;

public sealed class Saldo360DbContext(DbContextOptions<Saldo360DbContext> options)
    : DbContext(options), IDataProtectionKeyContext
{
    public DbSet<Saldo360.Domain.Workspaces.FinancialWorkspace> FinancialWorkspaces => Set<Saldo360.Domain.Workspaces.FinancialWorkspace>();
    public DbSet<Saldo360.Domain.ServiceAccounts.ServiceAccount> ServiceAccounts => Set<Saldo360.Domain.ServiceAccounts.ServiceAccount>();
    public DbSet<Saldo360.Domain.ServiceAccounts.ServiceAccountIdentifier> ServiceAccountIdentifiers => Set<Saldo360.Domain.ServiceAccounts.ServiceAccountIdentifier>();
    public DbSet<UserSession> UserSessions => Set<UserSession>();

    /// <summary>Chaves do Data Protection, compartilhadas entre instâncias e reinícios da API.</summary>
    public DbSet<DataProtectionKey> DataProtectionKeys => Set<DataProtectionKey>();

    public DbSet<ApplicationUser> Users => Set<ApplicationUser>();

    public DbSet<Family> Families => Set<Family>();

    public DbSet<FamilyMember> FamilyMembers => Set<FamilyMember>();

    public DbSet<FamilyInvitation> FamilyInvitations => Set<FamilyInvitation>();

    public DbSet<FinancialAccount> FinancialAccounts => Set<FinancialAccount>();

    public DbSet<FinancialCategory> FinancialCategories => Set<FinancialCategory>();

    public DbSet<FinancialTransaction> FinancialTransactions => Set<FinancialTransaction>();

    public DbSet<FinancialTransfer> FinancialTransfers => Set<FinancialTransfer>();

    public DbSet<FinancialPlannedTransaction> FinancialPlannedTransactions => Set<FinancialPlannedTransaction>();

    public DbSet<FinancialBudget> FinancialBudgets => Set<FinancialBudget>();

    public DbSet<FinancialBudgetTarget> FinancialBudgetTargets => Set<FinancialBudgetTarget>();
    public DbSet<Saldo360.Domain.Budgets.FinancialMonthlyAggregateTarget> FinancialMonthlyAggregateTargets => Set<Saldo360.Domain.Budgets.FinancialMonthlyAggregateTarget>();
    public DbSet<Saldo360.Domain.Bootstrap.FinancialBootstrapProvenance> FinancialBootstrapProvenance => Set<Saldo360.Domain.Bootstrap.FinancialBootstrapProvenance>();

    public DbSet<FinancialCreditCard> FinancialCreditCards => Set<FinancialCreditCard>();
    public DbSet<FinancialCreditCardPurchase> FinancialCreditCardPurchases => Set<FinancialCreditCardPurchase>();
    public DbSet<FinancialCreditCardInstallment> FinancialCreditCardInstallments => Set<FinancialCreditCardInstallment>();
    public DbSet<FinancialCreditCardInvoice> FinancialCreditCardInvoices => Set<FinancialCreditCardInvoice>();
    public DbSet<FinancialCreditCardInvoiceConsolidation> FinancialCreditCardInvoiceConsolidations => Set<FinancialCreditCardInvoiceConsolidation>();
    public DbSet<FinancialCreditCardInvoicePayment> FinancialCreditCardInvoicePayments => Set<FinancialCreditCardInvoicePayment>();
    public DbSet<FinancialPlannedInvoiceReconciliation> FinancialPlannedInvoiceReconciliations => Set<FinancialPlannedInvoiceReconciliation>();
    public DbSet<FinancialCreditCardImportBatch> FinancialCreditCardImportBatches => Set<FinancialCreditCardImportBatch>();
    public DbSet<FinancialCreditCardImportItem> FinancialCreditCardImportItems => Set<FinancialCreditCardImportItem>();
    public DbSet<FinancialCreditCardImportedInstallment> FinancialCreditCardImportedInstallments => Set<FinancialCreditCardImportedInstallment>();
    public DbSet<FinancialCreditCardInvoiceAdjustment> FinancialCreditCardInvoiceAdjustments => Set<FinancialCreditCardInvoiceAdjustment>();
    public DbSet<FinancialDocumentInboxItem> FinancialDocumentInboxItems => Set<FinancialDocumentInboxItem>();
    public DbSet<ServiceAccountBinding> ServiceAccountBindings => Set<ServiceAccountBinding>();
    public DbSet<FinancialEmailCollectorCheckpoint> FinancialEmailCollectorCheckpoints => Set<FinancialEmailCollectorCheckpoint>();
    public DbSet<FinancialRecurrence> FinancialRecurrences => Set<FinancialRecurrence>();
    public DbSet<FinancialRecurrenceMaterialization> FinancialRecurrenceMaterializations => Set<FinancialRecurrenceMaterialization>();
    public DbSet<FinancialPlanningScenario> FinancialPlanningScenarios => Set<FinancialPlanningScenario>();
    public DbSet<FinancialPlanningMonthlyParameter> FinancialPlanningMonthlyParameters => Set<FinancialPlanningMonthlyParameter>();
    public DbSet<FinancialDebt> FinancialDebts => Set<FinancialDebt>();
    public DbSet<FinancialDebtInstallment> FinancialDebtInstallments => Set<FinancialDebtInstallment>();
    public DbSet<FinancialDebtPayment> FinancialDebtPayments => Set<FinancialDebtPayment>();
    public DbSet<FinancialDebtInstallmentReconciliation> FinancialDebtInstallmentReconciliations => Set<FinancialDebtInstallmentReconciliation>();
    public DbSet<FinancialGoal> FinancialGoals => Set<FinancialGoal>();
    public DbSet<Saldo360.Domain.Pluggy.PluggyItemLink> PluggyItemLinks => Set<Saldo360.Domain.Pluggy.PluggyItemLink>();
    public DbSet<Saldo360.Domain.Pluggy.PluggySyncRun> PluggySyncRuns => Set<Saldo360.Domain.Pluggy.PluggySyncRun>();
    public DbSet<Saldo360.Domain.Pluggy.PluggyImportedTransaction> PluggyImportedTransactions => Set<Saldo360.Domain.Pluggy.PluggyImportedTransaction>();
    public DbSet<Saldo360.Domain.Pluggy.PluggyTransactionChange> PluggyTransactionChanges => Set<Saldo360.Domain.Pluggy.PluggyTransactionChange>();
    public DbSet<Saldo360.Domain.Pluggy.PluggyAccountLink> PluggyAccountLinks => Set<Saldo360.Domain.Pluggy.PluggyAccountLink>();
    public DbSet<Saldo360.Domain.Pluggy.PluggyPosting> PluggyPostings => Set<Saldo360.Domain.Pluggy.PluggyPosting>();
    public DbSet<Saldo360.Domain.Pluggy.PluggyCardBill> PluggyCardBills => Set<Saldo360.Domain.Pluggy.PluggyCardBill>();
    public DbSet<Saldo360.Domain.Pluggy.PluggyCardBillPayment> PluggyCardBillPayments => Set<Saldo360.Domain.Pluggy.PluggyCardBillPayment>();
    public DbSet<Saldo360.Domain.Accounts.FinancialAccountBalanceAnchor> FinancialAccountBalanceAnchors => Set<Saldo360.Domain.Accounts.FinancialAccountBalanceAnchor>();
    public DbSet<FinancialGoalAllocation> FinancialGoalAllocations => Set<FinancialGoalAllocation>();

    public override int SaveChanges(bool acceptAllChangesOnSuccess)
    {
        // Even a single INSERT needs an explicit transaction so the workspace root is
        // locked before child row locks, not only when EF happens to batch commands.
        Database.AutoTransactionBehavior = AutoTransactionBehavior.Always;
        return base.SaveChanges(acceptAllChangesOnSuccess);
    }

    public override Task<int> SaveChangesAsync(bool acceptAllChangesOnSuccess, CancellationToken cancellationToken = default)
    {
        Database.AutoTransactionBehavior = AutoTransactionBehavior.Always;
        return base.SaveChangesAsync(acceptAllChangesOnSuccess, cancellationToken);
    }

    /// <summary>Configuração do provedor usada pela API, pelas ferramentas de migration e pelos testes.</summary>
    public static DbContextOptionsBuilder Configure(DbContextOptionsBuilder builder, string connectionString) =>
        builder.UseNpgsql(connectionString).UseSnakeCaseNamingConvention()
            .AddInterceptors(new Saldo360.Infrastructure.Workspaces.FinancialWriteConnectionInterceptor(),
                new Saldo360.Infrastructure.Workspaces.FinancialWriteTransactionInterceptor());

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        FinancialCategoryConfiguration.ConfigureCollation(modelBuilder);
        modelBuilder.ApplyConfigurationsFromAssembly(typeof(Saldo360DbContext).Assembly);
    }
}
