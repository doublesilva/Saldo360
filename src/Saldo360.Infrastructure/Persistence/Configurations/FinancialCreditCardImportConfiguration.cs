using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using Saldo360.Domain.CreditCards;
using Saldo360.Domain.Families;
using Saldo360.Domain.Users;

namespace Saldo360.Infrastructure.Persistence.Configurations;

internal sealed class FinancialCreditCardImportConfiguration : IEntityTypeConfiguration<FinancialCreditCardImportBatch>, IEntityTypeConfiguration<FinancialCreditCardImportItem>
{
    public void Configure(EntityTypeBuilder<FinancialCreditCardImportBatch> builder)
    {
        builder.ToTable("financial_credit_card_import_batches", table => { table.HasCheckConstraint("ck_credit_card_import_batches_status", "status IN ('Completed')"); table.HasCheckConstraint("ck_credit_card_import_batches_source", "source IN ('Csv', 'Pdf')"); });
        builder.HasKey(item => item.Id); builder.Property(item => item.Id).ValueGeneratedNever();
        builder.Property(item => item.PayloadHash).HasMaxLength(64).IsRequired(); builder.Property(item => item.Status).HasConversion<string>().HasMaxLength(20); builder.Property(item => item.Source).HasConversion<string>().HasMaxLength(10);
        builder.Property(item => item.DocumentHash).HasMaxLength(64); builder.Property(item => item.DecisionHash).HasMaxLength(64); builder.Property(item => item.Parser).HasMaxLength(50); builder.Property(item => item.Competence).HasMaxLength(7);
        builder.Property(item => item.OfficialTotal).HasColumnType("numeric");
        builder.HasAlternateKey(item => new { item.FamilyId, item.Id });
        builder.HasIndex(item => item.PayloadHash);
        builder.HasIndex(item => new { item.FamilyId, item.CreditCardId, item.Source, item.DocumentHash }).IsUnique().HasFilter("document_hash IS NOT NULL");
        builder.HasOne<Family>().WithMany().HasForeignKey(item => item.FamilyId).OnDelete(DeleteBehavior.Restrict);
        builder.HasOne<FinancialCreditCard>().WithMany().HasForeignKey(item => new { item.FamilyId, item.CreditCardId }).HasPrincipalKey(card => new { card.FamilyId, card.Id }).OnDelete(DeleteBehavior.Restrict);
        builder.HasOne<ApplicationUser>().WithMany().HasForeignKey(item => item.CreatedByUserId).OnDelete(DeleteBehavior.Restrict);
        builder.Navigation(item => item.Items).UsePropertyAccessMode(PropertyAccessMode.Field);
    }

    public void Configure(EntityTypeBuilder<FinancialCreditCardImportItem> builder)
    {
        builder.ToTable("financial_credit_card_import_items", table => table.HasCheckConstraint("ck_credit_card_import_items_status", "status IN ('Imported', 'AlreadyExisting', 'Informational')"));
        builder.HasKey(item => item.Id); builder.Property(item => item.Id).ValueGeneratedNever();
        builder.Property(item => item.ExternalId).HasMaxLength(200); builder.Property(item => item.ComponentId).HasMaxLength(200); builder.Property(item => item.ComponentType).HasMaxLength(30); builder.Property(item => item.Status).HasConversion<string>().HasMaxLength(20);
        builder.HasIndex(item => new { item.ImportBatchId, item.RowNumber }).IsUnique();
        builder.HasIndex(item => new { item.FamilyId, item.CreditCardId, item.ExternalId }).IsUnique().HasFilter("external_id IS NOT NULL");
        builder.HasOne<FinancialCreditCard>().WithMany().HasForeignKey(item => new { item.FamilyId, item.CreditCardId }).HasPrincipalKey(card => new { card.FamilyId, card.Id }).OnDelete(DeleteBehavior.Restrict);
        builder.HasOne<FinancialCreditCardImportBatch>().WithMany(batch => batch.Items).HasForeignKey(item => new { item.FamilyId, item.ImportBatchId }).HasPrincipalKey(batch => new { batch.FamilyId, batch.Id }).OnDelete(DeleteBehavior.Restrict);
        builder.HasOne<FinancialCreditCardPurchase>().WithMany().HasForeignKey(item => new { item.FamilyId, item.PurchaseId }).HasPrincipalKey(purchase => new { purchase.FamilyId, purchase.Id }).OnDelete(DeleteBehavior.Restrict);
    }
}
