using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using Saldo360.Domain.CreditCards;

namespace Saldo360.Infrastructure.Persistence.Configurations;

internal sealed class FinancialDocumentInboxItemConfiguration : IEntityTypeConfiguration<FinancialDocumentInboxItem>
{
    public void Configure(EntityTypeBuilder<FinancialDocumentInboxItem> builder)
    {
        builder.ToTable("financial_document_inbox_items");
        builder.HasKey(item => new { item.FamilyId, item.Id });
        builder.Property(item => item.OriginalFileName).HasMaxLength(255).IsRequired();
        builder.Property(item => item.ContentHash).HasMaxLength(64).IsRequired();
        builder.Property(item => item.ContentType).HasMaxLength(100).IsRequired();
        builder.Property(item => item.StorageReference).HasMaxLength(200).IsRequired();
        builder.Property(item => item.ExternalSourceId).HasMaxLength(300);
        builder.Property(item => item.Parser).HasMaxLength(80);
        builder.Property(item => item.Competence).HasMaxLength(7);
        builder.Property(item => item.FailureCode).HasMaxLength(80);
        builder.Property(item => item.Source).HasConversion<string>().HasMaxLength(20);
        builder.Property(item => item.Status).HasConversion<string>().HasMaxLength(30);
        builder.HasIndex(item => new { item.FamilyId, item.ContentHash }).IsUnique();
        builder.HasIndex(item => new { item.FamilyId, item.Source, item.ExternalSourceId }).IsUnique()
            .HasFilter("external_source_id IS NOT NULL");
        builder.HasIndex(item => new { item.FamilyId, item.Status, item.ReceivedAt });
        builder.HasIndex(item => item.StorageReference).IsUnique();
        builder.HasOne<Saldo360.Domain.Families.Family>().WithMany().HasForeignKey(item => item.FamilyId).OnDelete(DeleteBehavior.Restrict);
        builder.HasOne<FinancialCreditCard>().WithMany().HasForeignKey(item => new { item.FamilyId, Id = item.CreditCardId })
            .HasPrincipalKey(card => new { card.FamilyId, card.Id }).IsRequired(false).OnDelete(DeleteBehavior.Restrict);
    }
}
