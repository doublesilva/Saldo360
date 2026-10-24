using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using Saldo360.Domain.CreditCards;
using Saldo360.Domain.Users;

namespace Saldo360.Infrastructure.Persistence.Configurations;

internal sealed class FinancialCreditCardInvoiceConsolidationConfiguration : IEntityTypeConfiguration<FinancialCreditCardInvoiceConsolidation>
{
    public void Configure(EntityTypeBuilder<FinancialCreditCardInvoiceConsolidation> b)
    {
        b.ToTable("financial_credit_card_invoice_consolidations", t => t.HasCheckConstraint("ck_official_consolidation_total", "official_total >= 0 AND scale(official_total) <= 2 AND version > 0"));
        b.HasKey(x => x.Id); b.Property(x => x.Id).ValueGeneratedNever();
        b.Property(x => x.DocumentHash).HasMaxLength(64); b.Property(x => x.DecisionHash).HasMaxLength(64); b.Property(x => x.StateHash).HasMaxLength(64);
        b.Property(x => x.SnapshotJson).HasColumnType("jsonb"); b.Property(x => x.OfficialTotal).HasColumnType("numeric");
        b.HasIndex(x => new { x.WorkspaceId, x.InvoiceId, x.Version }).IsUnique();
        b.HasIndex(x => new { x.WorkspaceId, x.CreditCardId, x.DocumentHash }).IsUnique();
        b.HasOne<FinancialCreditCardInvoice>().WithMany().HasForeignKey(x => new { x.WorkspaceId, x.InvoiceId }).HasPrincipalKey(x => new { x.WorkspaceId, x.Id }).OnDelete(DeleteBehavior.Restrict);
        b.HasOne<FinancialCreditCard>().WithMany().HasForeignKey(x => new { x.WorkspaceId, x.CreditCardId }).HasPrincipalKey(x => new { x.WorkspaceId, x.Id }).OnDelete(DeleteBehavior.Restrict);
        b.HasOne<FinancialDocumentInboxItem>().WithMany().HasForeignKey(x => new { x.WorkspaceId, x.DocumentId }).OnDelete(DeleteBehavior.Restrict);
        b.HasOne<ApplicationUser>().WithMany().HasForeignKey(x => x.CreatedByUserId).OnDelete(DeleteBehavior.Restrict);
    }
}
