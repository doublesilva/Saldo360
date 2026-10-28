using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using Saldo360.Domain.Pluggy;
using Saldo360.Domain.Users;
using Saldo360.Domain.Workspaces;

namespace Saldo360.Infrastructure.Persistence.Configurations;

internal sealed class PluggySyncRunConfiguration : IEntityTypeConfiguration<PluggySyncRun>
{
    public void Configure(EntityTypeBuilder<PluggySyncRun> b)
    {
        b.ToTable("pluggy_sync_runs", t =>
        {
            t.HasCheckConstraint("ck_pluggy_sync_runs_status", "status IN ('Running','Completed','CompletedWithErrors','Failed')");
            t.HasCheckConstraint("ck_pluggy_sync_runs_finished", "(status = 'Running') = (finished_at IS NULL)");
            t.HasCheckConstraint("ck_pluggy_sync_runs_counts", "seen >= 0 AND created >= 0 AND updated >= 0 AND unchanged >= 0 AND missing >= 0 AND errors >= 0");
        });
        b.HasKey(x => x.Id); b.Property(x => x.Id).ValueGeneratedNever();
        b.Property(x => x.Status).HasConversion<string>().HasMaxLength(24);
        b.Property(x => x.ErrorSummary).HasMaxLength(PluggySyncRun.ErrorSummaryMaxLength);
        // Uma única coleta em andamento por conexão: impede sincronizações simultâneas mesmo entre instâncias.
        b.HasIndex(x => x.ConnectionId).IsUnique().HasFilter("status = 'Running'").HasDatabaseName("ux_pluggy_sync_runs_running");
        b.HasIndex(x => new { x.ConnectionId, x.StartedAt });
        b.HasOne<FinancialWorkspace>().WithMany().HasForeignKey(x => x.WorkspaceId).OnDelete(DeleteBehavior.Restrict);
        b.HasOne<PluggyItemLink>().WithMany().HasForeignKey(x => x.ConnectionId).OnDelete(DeleteBehavior.Restrict);
        b.HasOne<ApplicationUser>().WithMany().HasForeignKey(x => x.UserId).OnDelete(DeleteBehavior.Restrict);
    }
}

internal sealed class PluggyImportedTransactionConfiguration : IEntityTypeConfiguration<PluggyImportedTransaction>
{
    public void Configure(EntityTypeBuilder<PluggyImportedTransaction> b)
    {
        b.ToTable("pluggy_imported_transactions", t =>
        {
            t.HasCheckConstraint("ck_pluggy_imported_transactions_status", "status IN ('Pending','Posted','Unknown')");
            t.HasCheckConstraint("ck_pluggy_imported_transactions_class", "classification IN ('InsufficientInformation','PotentialIncome','PotentialExpense','PotentialTransfer','CardPurchase','CardInstallment')");
            t.HasCheckConstraint("ck_pluggy_imported_transactions_missing", "missing_sync_count >= 0 AND (missing_sync_count = 0) = (first_missing_at IS NULL)");
        });
        b.HasKey(x => x.Id); b.Property(x => x.Id).ValueGeneratedNever();
        b.Property(x => x.PluggyAccountId).HasMaxLength(64).IsRequired();
        b.Property(x => x.PluggyTransactionId).HasMaxLength(64).IsRequired();
        b.Property(x => x.AccountType).HasMaxLength(16);
        b.Property(x => x.AccountLabel).HasMaxLength(160);
        b.Property(x => x.Amount).HasPrecision(19, 4);
        b.Property(x => x.Currency).HasMaxLength(8);
        b.Property(x => x.Description).HasMaxLength(PluggyImportedTransaction.TextMaxLength);
        b.Property(x => x.DescriptionRaw).HasMaxLength(PluggyImportedTransaction.TextMaxLength);
        b.Property(x => x.Type).HasMaxLength(16);
        b.Property(x => x.Status).HasConversion<string>().HasMaxLength(16);
        b.Property(x => x.OperationType).HasMaxLength(80);
        b.Property(x => x.Category).HasMaxLength(120);
        b.Property(x => x.BillId).HasMaxLength(80);
        b.Property(x => x.CardLast4).HasMaxLength(4);
        b.Property(x => x.PaymentMethod).HasMaxLength(40);
        b.Property(x => x.Classification).HasConversion<string>().HasMaxLength(32);
        b.Property(x => x.ClassificationReason).HasMaxLength(64).IsRequired();
        b.Property(x => x.PossibleDuplicateKey).HasMaxLength(64);
        // Identidade da transação importada: conexão + conta + ID externo.
        b.HasIndex(x => new { x.ConnectionId, x.PluggyAccountId, x.PluggyTransactionId }).IsUnique().HasDatabaseName("ux_pluggy_imported_transactions_external");
        b.HasIndex(x => new { x.WorkspaceId, x.ConnectionId, x.Date });
        b.HasIndex(x => new { x.ConnectionId, x.PluggyAccountId, x.LastSeenRunId });
        b.HasIndex(x => new { x.WorkspaceId, x.PossibleDuplicateKey }).HasFilter("possible_duplicate_key IS NOT NULL");
        b.HasOne<FinancialWorkspace>().WithMany().HasForeignKey(x => x.WorkspaceId).OnDelete(DeleteBehavior.Restrict);
        b.HasOne<PluggyItemLink>().WithMany().HasForeignKey(x => x.ConnectionId).OnDelete(DeleteBehavior.Restrict);
        b.HasOne<PluggySyncRun>().WithMany().HasForeignKey(x => x.FirstSeenRunId).OnDelete(DeleteBehavior.Restrict);
        b.HasOne<PluggySyncRun>().WithMany().HasForeignKey(x => x.LastSeenRunId).OnDelete(DeleteBehavior.Restrict);
        b.HasOne<PluggySyncRun>().WithMany().HasForeignKey(x => x.LastChangedRunId).OnDelete(DeleteBehavior.Restrict);
    }
}

internal sealed class PluggyTransactionChangeConfiguration : IEntityTypeConfiguration<PluggyTransactionChange>
{
    public void Configure(EntityTypeBuilder<PluggyTransactionChange> b)
    {
        b.ToTable("pluggy_transaction_changes");
        b.HasKey(x => x.Id); b.Property(x => x.Id).ValueGeneratedNever();
        b.Property(x => x.ChangedFields).HasMaxLength(200).IsRequired();
        b.Property(x => x.PreviousStatus).HasConversion<string>().HasMaxLength(16);
        b.Property(x => x.NewStatus).HasConversion<string>().HasMaxLength(16);
        b.Property(x => x.PreviousAmount).HasPrecision(19, 4);
        b.Property(x => x.NewAmount).HasPrecision(19, 4);
        b.HasIndex(x => x.SyncRunId);
        b.HasIndex(x => new { x.TransactionId, x.ObservedAt });
        b.HasOne<FinancialWorkspace>().WithMany().HasForeignKey(x => x.WorkspaceId).OnDelete(DeleteBehavior.Restrict);
        b.HasOne<PluggyImportedTransaction>().WithMany().HasForeignKey(x => x.TransactionId).OnDelete(DeleteBehavior.Restrict);
        b.HasOne<PluggySyncRun>().WithMany().HasForeignKey(x => x.SyncRunId).OnDelete(DeleteBehavior.Restrict);
    }
}
