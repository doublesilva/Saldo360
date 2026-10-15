using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using Saldo360.Domain.CreditCards;

namespace Saldo360.Infrastructure.Persistence.Configurations;

internal sealed class FinancialCreditCardInvoiceConfiguration : IEntityTypeConfiguration<FinancialCreditCardInvoice>
{
    public void Configure(EntityTypeBuilder<FinancialCreditCardInvoice> builder)
    {
        builder.ToTable("financial_credit_card_invoices");
        builder.HasKey(i => i.Id); builder.Property(i => i.Id).ValueGeneratedNever();
        builder.Property(i => i.Competence).HasMaxLength(7).IsFixedLength();
        builder.Property(i => i.CycleStartDate).HasColumnType("date"); builder.Property(i => i.ClosingDate).HasColumnType("date"); builder.Property(i => i.DueDate).HasColumnType("date");
        builder.HasIndex(i => new { i.FamilyId, i.CreditCardId, i.ClosingDate }).IsUnique();
        builder.HasAlternateKey(i => new { i.FamilyId, i.Id });
        builder.HasOne<FinancialCreditCard>().WithMany().HasForeignKey(i => new { i.FamilyId, i.CreditCardId }).HasPrincipalKey(c => new { c.FamilyId, c.Id }).OnDelete(DeleteBehavior.Restrict);
    }
}
