using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using Saldo360.Domain.CreditCards;
using Saldo360.Domain.Families;
using Saldo360.Domain.Planning;
using Saldo360.Domain.Users;

namespace Saldo360.Infrastructure.Persistence.Configurations;

internal sealed class FinancialPlannedInvoiceReconciliationConfiguration : IEntityTypeConfiguration<FinancialPlannedInvoiceReconciliation>
{
    public void Configure(EntityTypeBuilder<FinancialPlannedInvoiceReconciliation> builder)
    {
        builder.ToTable("financial_planned_invoice_reconciliations", table =>
        {
            table.HasCheckConstraint("ck_financial_planned_invoice_reconciliations_removed_pair",
                "(removed_at IS NULL) = (removed_by_user_id IS NULL)");
        });
        builder.HasKey(item => item.Id);
        builder.Property(item => item.Id).ValueGeneratedNever();
        builder.HasIndex(item => new { item.FamilyId, item.PlannedTransactionId })
            .HasFilter("removed_at IS NULL").IsUnique();
        builder.HasIndex(item => new { item.FamilyId, item.CreditCardInvoiceId })
            .HasFilter("removed_at IS NULL").IsUnique();
        builder.HasIndex(item => new { item.FamilyId, item.CreatedAt });
        builder.HasOne<Family>().WithMany().HasForeignKey(item => item.FamilyId).OnDelete(DeleteBehavior.Restrict);
        builder.HasOne<FinancialPlannedTransaction>().WithMany()
            .HasForeignKey(item => new { item.FamilyId, item.PlannedTransactionId })
            .HasPrincipalKey(item => new { item.FamilyId, item.Id })
            .OnDelete(DeleteBehavior.Restrict)
            .HasConstraintName("fk_reconciliations_planned_same_family");
        builder.HasOne<FinancialCreditCardInvoice>().WithMany()
            .HasForeignKey(item => new { item.FamilyId, item.CreditCardInvoiceId })
            .HasPrincipalKey(item => new { item.FamilyId, item.Id })
            .OnDelete(DeleteBehavior.Restrict)
            .HasConstraintName("fk_reconciliations_invoices_same_family");
        builder.HasOne<ApplicationUser>().WithMany().HasForeignKey(item => item.CreatedByUserId).OnDelete(DeleteBehavior.Restrict);
        builder.HasOne<ApplicationUser>().WithMany().HasForeignKey(item => item.RemovedByUserId).OnDelete(DeleteBehavior.Restrict);
    }
}
