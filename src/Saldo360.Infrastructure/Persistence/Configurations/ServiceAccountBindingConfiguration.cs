using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using Saldo360.Domain.CreditCards;
using Saldo360.Domain.Families;
using Saldo360.Domain.Recurrences;

namespace Saldo360.Infrastructure.Persistence.Configurations;

internal sealed class ServiceAccountBindingConfiguration : IEntityTypeConfiguration<ServiceAccountBinding>
{
    public void Configure(EntityTypeBuilder<ServiceAccountBinding> builder)
    {
        builder.ToTable("service_account_bindings", table => table.HasCheckConstraint("ck_service_account_bindings_reference",
            "account_reference_hash IS NOT NULL OR service_reference_hash IS NOT NULL"));
        builder.HasKey(x => x.Id); builder.Property(x => x.Id).ValueGeneratedNever();
        builder.Property(x => x.DocumentType).HasMaxLength(40).IsRequired();
        builder.Property(x => x.IssuerNormalized).HasMaxLength(200).IsRequired();
        builder.Property(x => x.AccountReferenceHash).HasMaxLength(64).IsFixedLength();
        builder.Property(x => x.ServiceReferenceHash).HasMaxLength(64).IsFixedLength();
        builder.Property(x => x.AccountReferenceMasked).HasMaxLength(40);
        builder.Property(x => x.ServiceReferenceMasked).HasMaxLength(40);
        builder.Property(x => x.Origin).HasMaxLength(40).IsRequired();
        builder.HasIndex(x => new { x.FamilyId, x.DocumentType, x.AccountReferenceHash }).IsUnique()
            .HasFilter("account_reference_hash IS NOT NULL");
        builder.HasIndex(x => new { x.FamilyId, x.DocumentType, x.ServiceReferenceHash }).IsUnique()
            .HasFilter("service_reference_hash IS NOT NULL");
        builder.HasIndex(x => new { x.FamilyId, x.RecurringTemplateId });
        builder.HasOne<Family>().WithMany().HasForeignKey(x => x.FamilyId).OnDelete(DeleteBehavior.Restrict);
        builder.HasOne<FinancialRecurrence>().WithMany().HasForeignKey(x => new { x.FamilyId, Id = x.RecurringTemplateId })
            .HasPrincipalKey(x => new { x.FamilyId, x.Id }).OnDelete(DeleteBehavior.Restrict);
    }
}
