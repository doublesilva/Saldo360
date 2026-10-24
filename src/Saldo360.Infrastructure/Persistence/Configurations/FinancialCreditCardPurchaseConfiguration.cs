using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using Saldo360.Domain.Categories;
using Saldo360.Domain.CreditCards;
using Saldo360.Domain.Workspaces;
using Saldo360.Domain.Users;

namespace Saldo360.Infrastructure.Persistence.Configurations;

internal sealed class FinancialCreditCardPurchaseConfiguration : IEntityTypeConfiguration<FinancialCreditCardPurchase>, IEntityTypeConfiguration<FinancialCreditCardInstallment>
{
    public void Configure(EntityTypeBuilder<FinancialCreditCardPurchase> builder)
    {
        builder.ToTable("financial_credit_card_purchases", table =>
        {
            table.HasCheckConstraint("ck_credit_card_purchases_description_not_blank", "btrim(description) <> ''");
            table.HasCheckConstraint("ck_credit_card_purchases_total_positive", "total_amount > 0");
            table.HasCheckConstraint("ck_credit_card_purchases_total_scale", "scale(total_amount) <= 2");
            table.HasCheckConstraint("ck_credit_card_purchases_installment_count", "installment_count BETWEEN 1 AND 120");
            table.HasCheckConstraint("ck_credit_card_purchases_status", "status IN ('Active', 'Cancelled')");
            table.HasCheckConstraint("ck_credit_card_purchases_paid_schedule", "previously_paid_installments >= 0 AND previously_paid_installments < installment_count AND (previously_paid_installments = 0 OR first_pending_competence IS NOT NULL) AND (first_pending_competence IS NULL OR EXTRACT(DAY FROM first_pending_competence) = 1)");
        });
        builder.HasKey(p => p.Id); builder.Property(p => p.Id).ValueGeneratedNever();
        builder.Property(p => p.Description).HasMaxLength(FinancialCreditCardPurchase.DescriptionMaxLength).IsRequired();
        builder.Property(p => p.TotalAmount).HasColumnType("numeric"); builder.Property(p => p.PurchaseDate).HasColumnType("date");
        builder.Property(p => p.Status).HasConversion<string>().HasMaxLength(20);
        builder.HasIndex(p => new { p.WorkspaceId, p.CreditCardId, p.PurchaseDate });
        builder.HasAlternateKey(p => new { p.WorkspaceId, p.Id });
        builder.HasOne<FinancialWorkspace>().WithMany().HasForeignKey(p => p.WorkspaceId).OnDelete(DeleteBehavior.Restrict);
        builder.HasOne<FinancialCreditCard>().WithMany().HasForeignKey(p => new { p.WorkspaceId, p.CreditCardId }).HasPrincipalKey(c => new { c.WorkspaceId, c.Id }).OnDelete(DeleteBehavior.Restrict).HasConstraintName("fk_credit_card_purchases_cards_same_family");
        builder.HasOne<FinancialCategory>().WithMany().HasForeignKey(p => new { p.WorkspaceId, p.CategoryId }).HasPrincipalKey(c => new { c.WorkspaceId, c.Id }).OnDelete(DeleteBehavior.Restrict).HasConstraintName("fk_credit_card_purchases_categories_same_family");
        builder.HasOne<ApplicationUser>().WithMany().HasForeignKey(p => p.CreatedByUserId).OnDelete(DeleteBehavior.Restrict);
        builder.HasOne<ApplicationUser>().WithMany().HasForeignKey(p => p.UpdatedByUserId).OnDelete(DeleteBehavior.Restrict);
        builder.HasOne<ApplicationUser>().WithMany().HasForeignKey(p => p.CancelledByUserId).OnDelete(DeleteBehavior.Restrict);
        builder.Navigation(p => p.Installments).UsePropertyAccessMode(PropertyAccessMode.Field);
    }

    public void Configure(EntityTypeBuilder<FinancialCreditCardInstallment> builder)
    {
        builder.ToTable("financial_credit_card_installments", table =>
        {
            table.HasCheckConstraint("ck_credit_card_installments_number_positive", "installment_number BETWEEN 1 AND 120");
            table.HasCheckConstraint("ck_credit_card_installments_amount_positive", "amount > 0");
            table.HasCheckConstraint("ck_credit_card_installments_amount_scale", "scale(amount) <= 2");
            table.HasCheckConstraint("ck_credit_card_installments_status", "status IN ('Active', 'Cancelled')");
        });
        builder.HasKey(i => i.Id); builder.Property(i => i.Id).ValueGeneratedNever();
        builder.Property(i => i.Amount).HasColumnType("numeric"); builder.Property(i => i.Status).HasConversion<string>().HasMaxLength(20);
        builder.HasIndex(i => new { i.WorkspaceId, i.PurchaseId, i.InstallmentNumber }).IsUnique();
        builder.HasOne<FinancialCategory>().WithMany().HasForeignKey(i => new { i.WorkspaceId, i.OfficialCategoryId }).HasPrincipalKey(c => new { c.WorkspaceId, c.Id }).OnDelete(DeleteBehavior.Restrict);
        builder.HasOne<FinancialCreditCardPurchase>().WithMany(p => p.Installments).HasForeignKey(i => new { i.WorkspaceId, i.PurchaseId }).HasPrincipalKey(p => new { p.WorkspaceId, p.Id }).OnDelete(DeleteBehavior.Restrict).HasConstraintName("fk_credit_card_installments_purchases_same_family");
        builder.HasOne<FinancialCreditCardInvoice>().WithMany().HasForeignKey(i => new { i.WorkspaceId, i.InvoiceId }).HasPrincipalKey(invoice => new { invoice.WorkspaceId, invoice.Id }).OnDelete(DeleteBehavior.Restrict).HasConstraintName("fk_credit_card_installments_invoices_same_family");
    }
}
