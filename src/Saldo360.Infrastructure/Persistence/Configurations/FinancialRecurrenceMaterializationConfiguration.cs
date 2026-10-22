using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using Saldo360.Domain.Workspaces;
using Saldo360.Domain.Planning;
using Saldo360.Domain.Recurrences;
using Saldo360.Domain.CreditCards;

namespace Saldo360.Infrastructure.Persistence.Configurations;

internal sealed class FinancialRecurrenceMaterializationConfiguration : IEntityTypeConfiguration<FinancialRecurrenceMaterialization>
{
    public void Configure(EntityTypeBuilder<FinancialRecurrenceMaterialization> b)
    {
        b.ToTable("financial_recurrence_materializations");
        b.ToTable(t => t.HasCheckConstraint("ck_recurrence_materialization_destination", "(planned_transaction_id IS NOT NULL) <> (credit_card_purchase_id IS NOT NULL)"));
        b.HasKey(x => x.Id); b.Property(x => x.Id).ValueGeneratedNever();
        b.Property(x => x.ScheduledDate).HasColumnType("date");
        b.HasIndex(x => new { x.WorkspaceId, x.RecurrenceId, x.OccurrenceNumber }).IsUnique();
        b.HasIndex(x => new { x.WorkspaceId, x.RecurrenceId, x.ScheduledDate });
        b.HasOne<FinancialWorkspace>().WithMany().HasForeignKey(x => x.WorkspaceId).OnDelete(DeleteBehavior.Restrict);
        b.HasOne<FinancialRecurrence>().WithMany().HasForeignKey(x => new { x.WorkspaceId, x.RecurrenceId }).HasPrincipalKey(x => new { x.WorkspaceId, x.Id }).OnDelete(DeleteBehavior.Restrict);
        b.HasOne<FinancialPlannedTransaction>().WithMany().HasForeignKey(x => new { x.WorkspaceId, x.PlannedTransactionId }).HasPrincipalKey(x => new { x.WorkspaceId, x.Id }).OnDelete(DeleteBehavior.Restrict);
        b.HasOne<FinancialCreditCardPurchase>().WithMany().HasForeignKey(x => new { x.WorkspaceId, x.CreditCardPurchaseId }).HasPrincipalKey(x => new { x.WorkspaceId, x.Id }).OnDelete(DeleteBehavior.Restrict);
    }
}
