using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using Saldo360.Domain.Accounts;
using Saldo360.Domain.Common;
using Saldo360.Domain.Workspaces;
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

            // Saldo inicial: valor e data juntos ou ambos nulos. Coluna numeric sem escala fixa de propósito:
            // numeric(p,2) arredondaria em silêncio 10.005 → 10.01; aqui o banco recusa mais de 2 casas.
            table.HasCheckConstraint("ck_financial_accounts_opening_balance_pair",
                "(opening_balance IS NULL) = (opening_balance_date IS NULL)");
            table.HasCheckConstraint("ck_financial_accounts_opening_balance_scale",
                $"scale(opening_balance) <= {Money.MaxScale}");
            table.HasCheckConstraint("ck_financial_accounts_opening_balance_range",
                $"abs(opening_balance) < {Money.Limit}");

            // Conta principal (T09.3.3): só conta bancária e ativa pode ser a principal.
            table.HasCheckConstraint("ck_financial_accounts_primary_eligible",
                "NOT is_primary OR (is_active AND type IN ('Checking', 'Savings'))");
        });

        builder.HasKey(account => account.Id);
        builder.Property(account => account.Id).ValueGeneratedNever();
        builder.Property(account => account.Name).HasMaxLength(FinancialAccount.NameMaxLength).IsRequired();
        builder.Property(account => account.Institution).HasMaxLength(FinancialAccount.InstitutionMaxLength);
        builder.Property(account => account.Type).HasConversion<string>().HasMaxLength(20);
        builder.Property(account => account.Scope).HasConversion<string>().HasMaxLength(2);
        builder.Property(account => account.OpeningBalance).HasColumnType("numeric");
        builder.Property(account => account.OpeningBalanceDate).HasColumnType("date");
        builder.Property(account => account.Currency).HasMaxLength(3).IsFixedLength();
        builder.HasIndex(account => new { account.WorkspaceId, account.IsActive });
        builder.Property(account => account.IsPrimary).HasDefaultValue(false);
        // No máximo uma conta principal por workspace; a troca atômica desmarca a anterior antes de marcar a nova.
        builder.HasIndex(account => account.WorkspaceId).IsUnique().HasFilter("is_primary").HasDatabaseName("ux_financial_accounts_primary");

        // Restrict em todas as FKs: contas nunca são apagadas por cascata (não há exclusão física).
        builder.HasOne<FinancialWorkspace>()
            .WithMany()
            .HasForeignKey(account => account.WorkspaceId)
            .OnDelete(DeleteBehavior.Restrict);
        builder.HasOne<ApplicationUser>()
            .WithMany()
            .HasForeignKey(account => account.CreatedByUserId)
            .OnDelete(DeleteBehavior.Restrict);

        // Personal: a responsible user, when assigned, must be the workspace owner.
        builder.HasOne<FinancialWorkspace>()
            .WithMany()
            .HasForeignKey(account => new { account.WorkspaceId, account.ResponsibleUserId })
            .HasPrincipalKey(workspace => new { workspace.Id, workspace.OwnerUserId })
            .HasConstraintName("fk_financial_accounts_workspace_responsible")
            .OnDelete(DeleteBehavior.Restrict);
    }
}
