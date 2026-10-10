using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using Saldo360.Domain.Accounts;
using Saldo360.Domain.Families;
using Saldo360.Domain.Users;

namespace Saldo360.Infrastructure.Persistence.Configurations;

internal sealed class FinancialAccountConfiguration : IEntityTypeConfiguration<FinancialAccount>
{
    public void Configure(EntityTypeBuilder<FinancialAccount> builder)
    {
        builder.ToTable("financial_accounts", table =>
        {
            table.HasCheckConstraint("ck_financial_accounts_type",
                $"type IN ({string.Join(", ", AccountTypes.Names.Select(name => $"'{name}'"))})");
            table.HasCheckConstraint("ck_financial_accounts_scope",
                $"scope IN ({string.Join(", ", FinancialScopes.Names.Select(name => $"'{name}'"))})");
            table.HasCheckConstraint("ck_financial_accounts_currency", $"currency = '{FinancialAccount.DefaultCurrency}'");
            table.HasCheckConstraint("ck_financial_accounts_name_not_blank", "btrim(name) <> ''");
        });

        builder.HasKey(account => account.Id);
        builder.Property(account => account.Id).ValueGeneratedNever();
        builder.Property(account => account.Name).HasMaxLength(FinancialAccount.NameMaxLength).IsRequired();
        builder.Property(account => account.Institution).HasMaxLength(FinancialAccount.InstitutionMaxLength);
        builder.Property(account => account.Type).HasConversion<string>().HasMaxLength(20);
        builder.Property(account => account.Scope).HasConversion<string>().HasMaxLength(2);
        builder.Property(account => account.Currency).HasMaxLength(3).IsFixedLength();
        builder.HasIndex(account => new { account.FamilyId, account.IsActive });

        // Restrict em todas as FKs: contas nunca são apagadas por cascata (não há exclusão física).
        builder.HasOne<Family>()
            .WithMany()
            .HasForeignKey(account => account.FamilyId)
            .OnDelete(DeleteBehavior.Restrict);
        builder.HasOne<ApplicationUser>()
            .WithMany()
            .HasForeignKey(account => account.CreatedByUserId)
            .OnDelete(DeleteBehavior.Restrict);

        // FK composta (family_id, responsible_member_id) → family_members (family_id, id): o banco só aceita
        // responsável que seja vínculo da MESMA família. Com responsible_member_id nulo (conta conjunta),
        // a FK não é verificada (MATCH SIMPLE).
        builder.HasOne<FamilyMember>()
            .WithMany()
            .HasForeignKey(account => new { account.FamilyId, account.ResponsibleMemberId })
            .HasPrincipalKey(member => new { member.FamilyId, member.Id })
            .HasConstraintName("fk_financial_accounts_family_members_responsible")
            .OnDelete(DeleteBehavior.Restrict);
    }
}
