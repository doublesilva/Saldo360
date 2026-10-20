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

    public DbSet<FinancialCreditCard> FinancialCreditCards => Set<FinancialCreditCard>();
    public DbSet<FinancialCreditCardPurchase> FinancialCreditCardPurchases => Set<FinancialCreditCardPurchase>();
    public DbSet<FinancialCreditCardInstallment> FinancialCreditCardInstallments => Set<FinancialCreditCardInstallment>();
    public DbSet<FinancialCreditCardInvoice> FinancialCreditCardInvoices => Set<FinancialCreditCardInvoice>();
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
    public DbSet<FinancialGoalAllocation> FinancialGoalAllocations => Set<FinancialGoalAllocation>();

    /// <summary>Configuração do provedor usada pela API, pelas ferramentas de migration e pelos testes.</summary>
    public static DbContextOptionsBuilder Configure(DbContextOptionsBuilder builder, string connectionString) =>
        builder.UseNpgsql(connectionString).UseSnakeCaseNamingConvention();

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        FinancialCategoryConfiguration.ConfigureCollation(modelBuilder);
        modelBuilder.ApplyConfigurationsFromAssembly(typeof(Saldo360DbContext).Assembly);
    }
}
