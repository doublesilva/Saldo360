using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using Saldo360.Domain.CreditCards;

namespace Saldo360.Infrastructure.Persistence.Configurations;

internal sealed class FinancialDocumentInboxItemConfiguration : IEntityTypeConfiguration<FinancialDocumentInboxItem>
{
    public void Configure(EntityTypeBuilder<FinancialDocumentInboxItem> builder)
    {
        builder.ToTable("financial_document_inbox_items", table =>
        {
            table.HasCheckConstraint("ck_financial_document_inbox_service_bill_confirmation_complete",
                "(service_bill_occurrence_id IS NULL AND service_bill_recurrence_id IS NULL AND service_bill_competence IS NULL AND service_bill_projected_amount IS NULL AND service_bill_confirmed_amount IS NULL AND service_bill_confirmed_at IS NULL) OR (service_bill_occurrence_id IS NOT NULL AND service_bill_recurrence_id IS NOT NULL AND service_bill_competence IS NOT NULL AND service_bill_projected_amount > 0 AND service_bill_confirmed_amount > 0 AND service_bill_confirmed_at IS NOT NULL)");
            table.HasCheckConstraint("ck_financial_document_inbox_service_bill_amount_scale",
                "service_bill_projected_amount IS NULL OR (scale(service_bill_projected_amount) <= 2 AND scale(service_bill_confirmed_amount) <= 2)");
        });
        builder.HasKey(item => new { item.FamilyId, item.Id });
        builder.Property(item => item.OriginalFileName).HasMaxLength(255).IsRequired();
        builder.Property(item => item.ContentHash).HasMaxLength(64).IsRequired();
        builder.Property(item => item.ContentType).HasMaxLength(100).IsRequired();
        builder.Property(item => item.StorageReference).HasMaxLength(200).IsRequired();
        builder.Property(item => item.ExternalSourceId).HasMaxLength(300);
        builder.Property(item => item.Parser).HasMaxLength(80);
        builder.Property(item => item.Competence).HasMaxLength(7);
        builder.Property(item => item.FailureCode).HasMaxLength(80);
        builder.Property(item => item.DocumentType).HasMaxLength(40);
        builder.Property(item => item.ClassificationConfidence).HasMaxLength(20);
        builder.Property(item => item.ClassificationSource).HasMaxLength(20);
        builder.Property(item => item.ClassificationFailureCode).HasMaxLength(80);
        builder.Property(item => item.ServiceBillCompetence).HasMaxLength(7);
        builder.Property(item => item.ServiceBillProjectedAmount).HasColumnType("numeric");
        builder.Property(item => item.ServiceBillConfirmedAmount).HasColumnType("numeric");
        builder.Property(item => item.Source).HasConversion<string>().HasMaxLength(20);
        builder.Property(item => item.Status).HasConversion<string>().HasMaxLength(30);
        builder.HasIndex(item => new { item.FamilyId, item.ContentHash }).IsUnique();
        builder.HasIndex(item => new { item.FamilyId, item.Source, item.ExternalSourceId }).IsUnique()
            .HasFilter("external_source_id IS NOT NULL");
        builder.HasIndex(item => new { item.FamilyId, item.Status, item.ReceivedAt });
        builder.HasIndex(item => item.StorageReference).IsUnique();
        builder.HasIndex(item => new { item.FamilyId, item.ServiceBillOccurrenceId }).IsUnique()
            .HasFilter("service_bill_occurrence_id IS NOT NULL");
        builder.HasOne<Saldo360.Domain.Families.Family>().WithMany().HasForeignKey(item => item.FamilyId).OnDelete(DeleteBehavior.Restrict);
        builder.HasOne<FinancialCreditCard>().WithMany().HasForeignKey(item => new { item.FamilyId, Id = item.CreditCardId })
            .HasPrincipalKey(card => new { card.FamilyId, card.Id }).IsRequired(false).OnDelete(DeleteBehavior.Restrict);
        builder.HasOne<Saldo360.Domain.Planning.FinancialPlannedTransaction>().WithMany()
            .HasForeignKey(item => new { item.FamilyId, Id = item.ServiceBillOccurrenceId })
            .HasPrincipalKey(planned => new { planned.FamilyId, planned.Id }).IsRequired(false).OnDelete(DeleteBehavior.Restrict);
        builder.HasOne<Saldo360.Domain.Recurrences.FinancialRecurrence>().WithMany()
            .HasForeignKey(item => new { item.FamilyId, Id = item.ServiceBillRecurrenceId })
            .HasPrincipalKey(recurrence => new { recurrence.FamilyId, recurrence.Id }).IsRequired(false).OnDelete(DeleteBehavior.Restrict);
    }
}
