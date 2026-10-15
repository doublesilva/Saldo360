using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using Saldo360.Domain.CreditCards;
using Saldo360.Domain.Families;
using Saldo360.Domain.Transactions;

namespace Saldo360.Infrastructure.Persistence.Configurations;

internal sealed class FinancialCreditCardInvoicePaymentConfiguration : IEntityTypeConfiguration<FinancialCreditCardInvoicePayment>
{
    public void Configure(EntityTypeBuilder<FinancialCreditCardInvoicePayment> builder)
    {
        builder.ToTable("financial_credit_card_invoice_payments", table =>
        {
            table.HasCheckConstraint("ck_credit_card_invoice_payments_amount_positive", "amount > 0");
            table.HasCheckConstraint("ck_credit_card_invoice_payments_amount_scale", "scale(amount) <= 2");
        });
        builder.HasKey(p => p.Id); builder.Property(p => p.Id).ValueGeneratedNever();
        builder.Property(p => p.Amount).HasColumnType("numeric");
        builder.Property(p => p.PaymentDate).HasColumnType("date");
        builder.Property(p => p.Status).HasConversion<string>().HasMaxLength(20);
        builder.HasIndex(p => new { p.FamilyId, p.InvoiceId, p.PaymentDate });
        builder.HasIndex(p => new { p.FamilyId, p.TransactionId }).IsUnique();
        builder.HasOne<Family>().WithMany().HasForeignKey(p => p.FamilyId).OnDelete(DeleteBehavior.Restrict);
        builder.HasOne<FinancialCreditCardInvoice>().WithMany().HasForeignKey(p => new { p.FamilyId, p.InvoiceId }).HasPrincipalKey(i => new { i.FamilyId, i.Id }).OnDelete(DeleteBehavior.Restrict);
        builder.HasOne<FinancialTransaction>().WithMany().HasForeignKey(p => new { p.FamilyId, p.TransactionId }).HasPrincipalKey(t => new { t.FamilyId, t.Id }).OnDelete(DeleteBehavior.Restrict);
    }
}
