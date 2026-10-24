using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using Saldo360.Domain.CreditCards;

namespace Saldo360.Infrastructure.Persistence.Configurations;

internal sealed class FinancialCreditCardInvoiceConfiguration : IEntityTypeConfiguration<FinancialCreditCardInvoice>
{
    public void Configure(EntityTypeBuilder<FinancialCreditCardInvoice> builder)
    {
        builder.ToTable("financial_credit_card_invoices", table => table.HasCheckConstraint("ck_invoice_official_state",
            "(official_version = 0 AND official_total IS NULL AND official_closed_at IS NULL) OR (official_version > 0 AND official_total >= 0 AND scale(official_total) <= 2 AND official_closed_at IS NOT NULL)"));
        builder.HasKey(i => i.Id); builder.Property(i => i.Id).ValueGeneratedNever();
        builder.Property(i => i.Competence).HasMaxLength(7).IsFixedLength();
        builder.Property(i => i.OfficialTotal).HasColumnType("numeric");
        builder.Property(i => i.CycleStartDate).HasColumnType("date"); builder.Property(i => i.ClosingDate).HasColumnType("date"); builder.Property(i => i.DueDate).HasColumnType("date");
        builder.HasIndex(i => new { i.WorkspaceId, i.CreditCardId, i.ClosingDate }).IsUnique();
        builder.HasAlternateKey(i => new { i.WorkspaceId, i.Id });
        builder.HasOne<FinancialCreditCard>().WithMany().HasForeignKey(i => new { i.WorkspaceId, i.CreditCardId }).HasPrincipalKey(c => new { c.WorkspaceId, c.Id }).OnDelete(DeleteBehavior.Restrict);
    }
}
