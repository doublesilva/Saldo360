using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using Saldo360.Domain.Accounts;
using Saldo360.Domain.Categories;
using Saldo360.Domain.Common;
using Saldo360.Domain.Families;
using Saldo360.Domain.Planning;
using Saldo360.Domain.Users;

namespace Saldo360.Infrastructure.Persistence.Configurations;

internal sealed class FinancialPlannedTransactionConfiguration : IEntityTypeConfiguration<FinancialPlannedTransaction>
{
    public void Configure(EntityTypeBuilder<FinancialPlannedTransaction> builder)
    {
        builder.ToTable("financial_planned_transactions", table =>
        {
            table.HasCheckConstraint("ck_financial_planned_transactions_type",
                $"type IN ({string.Join(", ", CategoryTypes.Names.Select(name => $"'{name}'"))})");
            table.HasCheckConstraint("ck_financial_planned_transactions_currency", $"currency = '{FinancialPlannedTransaction.DefaultCurrency}'");
            table.HasCheckConstraint("ck_financial_planned_transactions_description_not_blank", "btrim(description) <> ''");
            table.HasCheckConstraint("ck_financial_planned_transactions_description_trimmed",
                "octet_length(description) = octet_length(btrim(description))");

            // Mesmas regras Money dos lançamentos: positivo, até 2 casas (numeric sem escala fixa, sem arredondar).
            table.HasCheckConstraint("ck_financial_planned_transactions_amount_positive", "amount > 0");
            table.HasCheckConstraint("ck_financial_planned_transactions_amount_scale", $"scale(amount) <= {Money.MaxScale}");
            table.HasCheckConstraint("ck_financial_planned_transactions_amount_range", $"amount < {Money.Limit}");

            table.HasCheckConstraint("ck_financial_planned_transactions_updated_pair",
                "(updated_at IS NULL) = (updated_by_user_id IS NULL)");
            table.HasCheckConstraint("ck_financial_planned_transactions_cancelled_at", "is_cancelled = (cancelled_at IS NOT NULL)");
            table.HasCheckConstraint("ck_financial_planned_transactions_cancelled_pair",
                "(cancelled_at IS NULL) = (cancelled_by_user_id IS NULL)");
        });

        builder.HasKey(planned => planned.Id);
        builder.Property(planned => planned.Id).ValueGeneratedNever();
        builder.Property(planned => planned.Type).HasConversion<string>().HasMaxLength(20);
        builder.Property(planned => planned.Description).HasMaxLength(FinancialPlannedTransaction.DescriptionMaxLength).IsRequired();
        builder.Property(planned => planned.Amount).HasColumnType("numeric");
        builder.Property(planned => planned.DueDate).HasColumnType("date");
        builder.Property(planned => planned.Currency).HasMaxLength(3).IsFixedLength();

        // Listagem: data prevista crescente e Id crescente (ordem estável), sempre filtrada por família.
        builder.HasIndex(planned => new { planned.FamilyId, planned.DueDate, planned.Id });

        // Restrict em todas as FKs: nada apaga previsões por cascata (histórico preservado).
        builder.HasOne<Family>()
            .WithMany()
            .HasForeignKey(planned => planned.FamilyId)
            .OnDelete(DeleteBehavior.Restrict);

        // (family_id, account_id) → financial_accounts (family_id, id): conta da MESMA família.
        builder.HasOne<FinancialAccount>()
            .WithMany()
            .HasForeignKey(planned => new { planned.FamilyId, planned.AccountId })
            .HasPrincipalKey(account => new { account.FamilyId, account.Id })
            .HasConstraintName("fk_financial_planned_transactions_accounts_same_family")
            .OnDelete(DeleteBehavior.Restrict);

        // (family_id, category_id, type) → financial_categories (family_id, id, type): MESMA família e MESMO tipo.
        builder.HasOne<FinancialCategory>()
            .WithMany()
            .HasForeignKey(planned => new { planned.FamilyId, planned.CategoryId, planned.Type })
            .HasPrincipalKey(category => new { category.FamilyId, category.Id, category.Type })
            .HasConstraintName("fk_financial_planned_transactions_categories_same_family_type")
            .OnDelete(DeleteBehavior.Restrict);

        builder.HasOne<ApplicationUser>()
            .WithMany()
            .HasForeignKey(planned => planned.CreatedByUserId)
            .OnDelete(DeleteBehavior.Restrict);
        builder.HasOne<ApplicationUser>()
            .WithMany()
            .HasForeignKey(planned => planned.UpdatedByUserId)
            .OnDelete(DeleteBehavior.Restrict);
        builder.HasOne<ApplicationUser>()
            .WithMany()
            .HasForeignKey(planned => planned.CancelledByUserId)
            .OnDelete(DeleteBehavior.Restrict);
    }
}
