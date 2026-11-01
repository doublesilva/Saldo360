using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using Saldo360.Domain.Planning;
using Saldo360.Domain.Recurrences;
using Saldo360.Domain.Transactions;
using Saldo360.Domain.Workspaces;

namespace Saldo360.Infrastructure.Persistence.Configurations;

internal sealed class FinancialObligationSettlementConfiguration : IEntityTypeConfiguration<FinancialObligationSettlement>
{
    public void Configure(EntityTypeBuilder<FinancialObligationSettlement> b)
    {
        b.ToTable("financial_obligation_settlements", t => t.HasCheckConstraint("ck_obligation_settlement_identity", "recurrence_id IS NOT NULL OR planned_transaction_id IS NOT NULL"));
        b.HasKey(x => x.Id); b.Property(x => x.Id).ValueGeneratedNever();
        b.Property(x => x.Competence).HasColumnType("date");
        b.Property(x => x.Origin).HasConversion<string>().HasMaxLength(16);
        // Cada ocorrência mensal admite um único pagamento integral; uma movimentação não quita duas obrigações.
        b.HasIndex(x => new { x.WorkspaceId, x.RecurrenceId, x.Competence }).IsUnique().HasFilter("removed_at IS NULL AND recurrence_id IS NOT NULL").HasDatabaseName("ux_obligation_settlement_recurrence_competence");
        b.HasIndex(x => new { x.WorkspaceId, x.PlannedTransactionId }).IsUnique().HasFilter("removed_at IS NULL AND planned_transaction_id IS NOT NULL").HasDatabaseName("ux_obligation_settlement_planned");
        b.HasIndex(x => new { x.WorkspaceId, x.FinancialTransactionId }).IsUnique().HasFilter("removed_at IS NULL").HasDatabaseName("ux_obligation_settlement_transaction");
        b.HasOne<FinancialWorkspace>().WithMany().HasForeignKey(x => x.WorkspaceId).OnDelete(DeleteBehavior.Restrict);
        b.HasOne<FinancialRecurrence>().WithMany().HasForeignKey(x => new { x.WorkspaceId, x.RecurrenceId }).HasPrincipalKey(x => new { x.WorkspaceId, x.Id }).OnDelete(DeleteBehavior.Restrict);
        b.HasOne<FinancialPlannedTransaction>().WithMany().HasForeignKey(x => new { x.WorkspaceId, x.PlannedTransactionId }).HasPrincipalKey(x => new { x.WorkspaceId, x.Id }).OnDelete(DeleteBehavior.Restrict);
        b.HasOne<FinancialTransaction>().WithMany().HasForeignKey(x => new { x.WorkspaceId, x.FinancialTransactionId }).HasPrincipalKey(x => new { x.WorkspaceId, x.Id }).OnDelete(DeleteBehavior.Restrict);
    }
}
