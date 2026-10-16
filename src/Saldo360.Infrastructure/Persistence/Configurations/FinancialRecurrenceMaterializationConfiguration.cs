using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using Saldo360.Domain.Families;
using Saldo360.Domain.Planning;
using Saldo360.Domain.Recurrences;

namespace Saldo360.Infrastructure.Persistence.Configurations;

internal sealed class FinancialRecurrenceMaterializationConfiguration : IEntityTypeConfiguration<FinancialRecurrenceMaterialization>
{
    public void Configure(EntityTypeBuilder<FinancialRecurrenceMaterialization> b)
    {
        b.ToTable("financial_recurrence_materializations");
        b.HasKey(x => x.Id); b.Property(x => x.Id).ValueGeneratedNever();
        b.Property(x => x.ScheduledDate).HasColumnType("date");
        b.HasIndex(x => new { x.FamilyId, x.RecurrenceId, x.OccurrenceNumber }).IsUnique();
        b.HasIndex(x => new { x.FamilyId, x.RecurrenceId, x.ScheduledDate });
        b.HasOne<Family>().WithMany().HasForeignKey(x => x.FamilyId).OnDelete(DeleteBehavior.Restrict);
        b.HasOne<FinancialRecurrence>().WithMany().HasForeignKey(x => new { x.FamilyId, x.RecurrenceId }).HasPrincipalKey(x => new { x.FamilyId, x.Id }).OnDelete(DeleteBehavior.Restrict);
        b.HasOne<FinancialPlannedTransaction>().WithMany().HasForeignKey(x => x.PlannedTransactionId).OnDelete(DeleteBehavior.Restrict);
    }
}
