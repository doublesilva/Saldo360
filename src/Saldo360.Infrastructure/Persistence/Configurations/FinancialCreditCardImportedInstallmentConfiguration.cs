using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using Saldo360.Domain.Categories;
using Saldo360.Domain.CreditCards;
using Saldo360.Domain.Workspaces;
using Saldo360.Domain.Users;

namespace Saldo360.Infrastructure.Persistence.Configurations;

internal sealed class FinancialCreditCardImportedInstallmentConfiguration : IEntityTypeConfiguration<FinancialCreditCardImportedInstallment>
{
    public void Configure(EntityTypeBuilder<FinancialCreditCardImportedInstallment> builder)
    {
        builder.ToTable("financial_credit_card_imported_installments", table =>
        {
            table.HasCheckConstraint("ck_imported_installments_amount_positive", "amount > 0");
            table.HasCheckConstraint("ck_imported_installments_amount_scale", "scale(amount) <= 2");
            table.HasCheckConstraint("ck_imported_installments_number", "(installment_number IS NULL OR installment_number >= 1) AND (installment_count IS NULL OR installment_count >= 1) AND (installment_number IS NULL OR installment_count IS NULL OR installment_number <= installment_count)");
            table.HasCheckConstraint("ck_imported_installments_known", "invoice_id IS NOT NULL OR (is_historical AND competence IS NOT NULL AND source_system IS NOT NULL AND external_reference IS NOT NULL)");
            table.HasCheckConstraint("ck_imported_installments_legacy_fields", "source_system IS NOT NULL OR (transaction_date IS NOT NULL AND installment_number IS NOT NULL AND installment_count IS NOT NULL AND import_id IS NOT NULL AND row_number IS NOT NULL)");
            table.HasCheckConstraint("ck_imported_installments_competence", "competence IS NULL OR EXTRACT(DAY FROM competence) = 1");
            table.HasCheckConstraint("ck_imported_installments_status", "status IN ('Active', 'Cancelled')");
            table.HasCheckConstraint("ck_imported_installments_mode", "(is_historical = true AND linked_installment_id IS NULL) OR (is_historical = false AND linked_installment_id IS NOT NULL)");
        });
        builder.HasKey(item => item.Id); builder.Property(item => item.Id).ValueGeneratedNever();
        builder.Property(item => item.Description).HasMaxLength(200).IsRequired(); builder.Property(item => item.ExternalId).HasMaxLength(200);
        builder.Property(item => item.Amount).HasColumnType("numeric"); builder.Property(item => item.TransactionDate).HasColumnType("date");
        builder.Property(item => item.Status).HasConversion<string>().HasMaxLength(20);
        builder.Property(item => item.SourceSystem).HasMaxLength(80);
        builder.Property(item => item.ExternalReference).HasMaxLength(200);
        builder.Property(item => item.Competence).HasColumnType("date");
        builder.Property(item => item.DueDate).HasColumnType("date");
        builder.HasIndex(item => new { item.WorkspaceId, item.CreditCardId, item.ImportId, item.RowNumber }).IsUnique();
        builder.HasIndex(item => new { item.WorkspaceId, item.CreditCardId, item.ExternalId }).IsUnique().HasFilter("external_id IS NOT NULL AND status = 'Active'");
        builder.HasIndex(item => new { item.WorkspaceId, item.LinkedInstallmentId }).IsUnique().HasFilter("linked_installment_id IS NOT NULL AND status = 'Active'");
        builder.HasOne<FinancialWorkspace>().WithMany().HasForeignKey(item => item.WorkspaceId).OnDelete(DeleteBehavior.Restrict);
        builder.HasOne<FinancialCreditCard>().WithMany().HasForeignKey(item => new { item.WorkspaceId, item.CreditCardId }).HasPrincipalKey(card => new { card.WorkspaceId, card.Id }).OnDelete(DeleteBehavior.Restrict);
        builder.HasOne<FinancialCreditCardInvoice>().WithMany().HasForeignKey(item => new { item.WorkspaceId, item.InvoiceId }).HasPrincipalKey(invoice => new { invoice.WorkspaceId, invoice.Id }).OnDelete(DeleteBehavior.Restrict);
        builder.HasOne<FinancialCreditCardInstallment>().WithMany().HasForeignKey(item => new { item.WorkspaceId, item.LinkedInstallmentId }).HasPrincipalKey(installment => new { installment.WorkspaceId, installment.Id }).OnDelete(DeleteBehavior.Restrict);
        builder.HasOne<FinancialCategory>().WithMany().HasForeignKey(item => new { item.WorkspaceId, item.CategoryId }).HasPrincipalKey(category => new { category.WorkspaceId, category.Id }).OnDelete(DeleteBehavior.Restrict);
        builder.HasOne<ApplicationUser>().WithMany().HasForeignKey(item => item.CreatedByUserId).OnDelete(DeleteBehavior.Restrict);
        builder.HasOne<ApplicationUser>().WithMany().HasForeignKey(item => item.CancelledByUserId).OnDelete(DeleteBehavior.Restrict);
    }
}
