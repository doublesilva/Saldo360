using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using Saldo360.Domain.ServiceAccounts;
using Saldo360.Domain.Users;

namespace Saldo360.Infrastructure.Persistence.Configurations;

internal sealed class ServiceAccountConfiguration : IEntityTypeConfiguration<ServiceAccount>
{
    public void Configure(EntityTypeBuilder<ServiceAccount> b)
    {
        b.ToTable("service_accounts", t =>
        {
            t.HasCheckConstraint("ck_service_accounts_type", "service_type IN ('Electricity','Water','Telecom')");
            t.HasCheckConstraint("ck_service_accounts_names", "btrim(issuer) <> '' AND btrim(display_name) <> ''");
        });
        b.HasKey(a => a.Id); b.Property(a => a.Id).ValueGeneratedNever();
        b.HasAlternateKey(a => new { a.Id, a.UserId, a.ServiceType });
        b.Property(a => a.ServiceType).HasConversion<string>().HasMaxLength(20);
        b.Property(a => a.Issuer).HasMaxLength(120).IsRequired(); b.Property(a => a.DisplayName).HasMaxLength(160).IsRequired();
        b.HasIndex(a => new { a.UserId, a.ServiceType, a.IsActive });
        b.HasOne<ApplicationUser>().WithMany().HasForeignKey(a => a.UserId).OnDelete(DeleteBehavior.Restrict);
    }
}
internal sealed class ServiceAccountIdentifierConfiguration : IEntityTypeConfiguration<ServiceAccountIdentifier>
{
    public void Configure(EntityTypeBuilder<ServiceAccountIdentifier> b)
    {
        b.ToTable("service_account_identifiers", t =>
        {
            t.HasCheckConstraint("ck_service_identifiers_type", "type IN ('UtilityCustomerNumber','CustomerCode','InstallationNumber','ContractNumber','Keyword')");
            t.HasCheckConstraint("ck_service_identifiers_protection", "octet_length(protected_normalized_value) > 0 AND lookup_hash ~ '^[0-9a-f]{64}$' AND btrim(masked_value) <> ''");
        });
        b.HasKey(i => i.Id); b.Property(i => i.Id).ValueGeneratedNever();
        b.Property(i => i.ServiceType).HasConversion<string>().HasMaxLength(20); b.Property(i => i.Type).HasConversion<string>().HasMaxLength(30);
        b.Property(i => i.ProtectedNormalizedValue).IsRequired(); b.Property(i => i.LookupHash).HasMaxLength(64).IsRequired(); b.Property(i => i.MaskedValue).HasMaxLength(84).IsRequired();
        b.HasIndex(i => new { i.UserId, i.ServiceType, i.Type, i.LookupHash }).IsUnique().HasFilter("is_active AND type <> 'Keyword'").HasDatabaseName("ux_service_identifiers_owner_type_hash_active");
        b.HasOne<ServiceAccount>().WithMany().HasForeignKey(i => new { i.ServiceAccountId, i.UserId, i.ServiceType })
            .HasPrincipalKey(a => new { a.Id, a.UserId, a.ServiceType }).OnDelete(DeleteBehavior.Restrict);
    }
}
