using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using Saldo360.Domain.Categories;
using Saldo360.Domain.CreditCards;
using Saldo360.Domain.Workspaces;
using Saldo360.Domain.Users;

namespace Saldo360.Infrastructure.Persistence.Configurations;

internal sealed class FinancialCreditCardInvoiceAdjustmentConfiguration
    : IEntityTypeConfiguration<FinancialCreditCardInvoiceAdjustment>
{
    public void Configure(EntityTypeBuilder<FinancialCreditCardInvoiceAdjustment> builder)
    {
        builder.ToTable("financial_credit_card_invoice_adjustments", table =>
        {
            table.HasCheckConstraint("ck_invoice_adjustments_amount_positive", "amount > 0");
            table.HasCheckConstraint("ck_invoice_adjustments_amount_scale", "scale(amount) <= 2");
            table.HasCheckConstraint("ck_invoice_adjustments_type", "type IN ('Fee', 'Credit', 'Refund', 'PreviousBalance', 'FinancedBalance')");
            table.HasCheckConstraint("ck_invoice_adjustments_nature", "nature IN ('Debit', 'Credit')");
            table.HasCheckConstraint("ck_invoice_adjustments_status", "status IN ('Active', 'Cancelled')");
            table.HasCheckConstraint("ck_invoice_adjustments_type_nature", "(type IN ('Fee', 'PreviousBalance', 'FinancedBalance') AND nature = 'Debit') OR (type IN ('Credit', 'Refund') AND nature = 'Credit')");
        });
        builder.HasKey(item => item.Id);
        builder.Property(item => item.Id).ValueGeneratedNever();
        builder.Property(item => item.Description).HasMaxLength(FinancialCreditCardInvoiceAdjustment.DescriptionMaxLength).IsRequired();
        builder.Property(item => item.ExternalId).HasMaxLength(FinancialCreditCardInvoiceAdjustment.ExternalIdMaxLength).IsRequired();
        builder.Property(item => item.Amount).HasColumnType("numeric");
        builder.Property(item => item.TransactionDate).HasColumnType("date");
        builder.Property(item => item.Type).HasConversion<string>().HasMaxLength(20);
        builder.Property(item => item.Nature).HasConversion<string>().HasMaxLength(20);
        builder.Property(item => item.Status).HasConversion<string>().HasMaxLength(20);
        builder.Ignore(item => item.SignedAmount);
        builder.HasIndex(item => new { item.WorkspaceId, item.CreditCardId, item.ExternalId }).IsUnique();
        builder.HasIndex(item => new { item.WorkspaceId, item.InvoiceId, item.Status });
        builder.HasOne<FinancialWorkspace>().WithMany().HasForeignKey(item => item.WorkspaceId).OnDelete(DeleteBehavior.Restrict);
        builder.HasOne<FinancialCreditCard>().WithMany().HasForeignKey(item => new { item.WorkspaceId, item.CreditCardId }).HasPrincipalKey(card => new { card.WorkspaceId, card.Id }).OnDelete(DeleteBehavior.Restrict);
        builder.HasOne<FinancialCreditCardInvoice>().WithMany().HasForeignKey(item => new { item.WorkspaceId, item.InvoiceId }).HasPrincipalKey(invoice => new { invoice.WorkspaceId, invoice.Id }).OnDelete(DeleteBehavior.Restrict);
        builder.HasOne<FinancialCategory>().WithMany().HasForeignKey(item => new { item.WorkspaceId, item.CategoryId }).HasPrincipalKey(category => new { category.WorkspaceId, category.Id }).OnDelete(DeleteBehavior.Restrict);
        builder.HasOne<ApplicationUser>().WithMany().HasForeignKey(item => item.CreatedByUserId).OnDelete(DeleteBehavior.Restrict);
        builder.HasOne<ApplicationUser>().WithMany().HasForeignKey(item => item.CancelledByUserId).OnDelete(DeleteBehavior.Restrict);
    }
}
