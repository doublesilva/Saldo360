using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using Saldo360.Domain.Accounts;
using Saldo360.Domain.Categories;
using Saldo360.Domain.Common;
using Saldo360.Domain.Workspaces;
using Saldo360.Domain.Planning;
using Saldo360.Domain.Transactions;
using Saldo360.Domain.Users;

namespace Saldo360.Infrastructure.Persistence.Configurations;

internal sealed class FinancialTransactionConfiguration : IEntityTypeConfiguration<FinancialTransaction>
{
    public const string ActiveRealizationIndex = "ux_financial_transactions_active_realization";

    public void Configure(EntityTypeBuilder<FinancialTransaction> builder)
    {
        builder.ToTable("financial_transactions", table =>
        {
            table.HasCheckConstraint("ck_financial_transactions_type",
                $"type IN ({string.Join(", ", CategoryTypes.Names.Select(name => $"'{name}'"))})");
            table.HasCheckConstraint("ck_financial_transactions_currency", $"currency = '{FinancialTransaction.DefaultCurrency}'");
            table.HasCheckConstraint("ck_financial_transactions_description_not_blank", "btrim(description) <> ''");
            table.HasCheckConstraint("ck_financial_transactions_description_trimmed",
                "octet_length(description) = octet_length(btrim(description))");

            // Valor sempre positivo (o tipo dá o sentido). Coluna numeric sem escala fixa, como o saldo inicial:
            // numeric(p,2) arredondaria em silêncio; aqui o banco recusa mais de 2 casas.
            table.HasCheckConstraint("ck_financial_transactions_amount_positive", "amount > 0");
            table.HasCheckConstraint("ck_financial_transactions_amount_scale", $"scale(amount) <= {Money.MaxScale}");
            table.HasCheckConstraint("ck_financial_transactions_amount_range", $"amount < {Money.Limit}");

            table.HasCheckConstraint("ck_financial_transactions_updated_pair",
                "(updated_at IS NULL) = (updated_by_user_id IS NULL)");
            table.HasCheckConstraint("ck_financial_transactions_cancelled_at",
                "is_cancelled = (cancelled_at IS NOT NULL)");
            table.HasCheckConstraint("ck_financial_transactions_cancelled_pair",
                "(cancelled_at IS NULL) = (cancelled_by_user_id IS NULL)");
        });

        builder.HasKey(transaction => transaction.Id);
        builder.Property(transaction => transaction.Id).ValueGeneratedNever();
        builder.Property(transaction => transaction.Type).HasConversion<string>().HasMaxLength(20);
        builder.Property(transaction => transaction.Origin).HasConversion<string>().HasMaxLength(40);
        builder.Property(transaction => transaction.Nature).HasColumnName("expense_nature").HasConversion<string>().HasMaxLength(20).HasDefaultValue(ExpenseNature.Undefined);
        builder.ToTable(table => table.HasCheckConstraint("ck_financial_transactions_expense_nature", ExpenseNatureCheck.Sql("type", "expense_nature")));
        builder.Property(transaction => transaction.Description).HasMaxLength(FinancialTransaction.DescriptionMaxLength).IsRequired();
        builder.Property(transaction => transaction.Amount).HasColumnType("numeric");
        builder.Property(transaction => transaction.TransactionDate).HasColumnType("date");
        builder.Property(transaction => transaction.Currency).HasMaxLength(3).IsFixedLength();

        // Listagem: data decrescente e Id decrescente (ordem estável), sempre filtrada por família.
        builder.HasIndex(transaction => new { transaction.WorkspaceId, transaction.TransactionDate, transaction.Id })
            .IsDescending(false, true, true);

        // Restrict em todas as FKs: nada apaga lançamentos por cascata (histórico preservado).
        builder.HasOne<FinancialWorkspace>()
            .WithMany()
            .HasForeignKey(transaction => transaction.WorkspaceId)
            .OnDelete(DeleteBehavior.Restrict);

        // (workspace_id, account_id) → financial_accounts (workspace_id, id): conta da MESMA família.
        builder.HasOne<FinancialAccount>()
            .WithMany()
            .HasForeignKey(transaction => new { transaction.WorkspaceId, transaction.AccountId })
            .HasPrincipalKey(account => new { account.WorkspaceId, account.Id })
            .HasConstraintName("fk_financial_transactions_accounts_same_family")
            .OnDelete(DeleteBehavior.Restrict);

        // (workspace_id, category_id, type) → financial_categories (workspace_id, id, type): categoria da MESMA
        // família e do MESMO tipo do lançamento. O tipo da categoria é imutável, então a FK nunca é quebrada.
        builder.HasOne<FinancialCategory>()
            .WithMany()
            .HasForeignKey(transaction => new { transaction.WorkspaceId, transaction.CategoryId, transaction.Type })
            .HasPrincipalKey(category => new { category.WorkspaceId, category.Id, category.Type })
            .HasConstraintName("fk_financial_transactions_categories_same_family_type")
            .OnDelete(DeleteBehavior.Restrict);

        // Realização de previsão: (workspace_id, planned_transaction_id) → financial_planned_transactions (workspace_id, id),
        // previsão da MESMA família. Nulo = lançamento avulso (FK não verificada, MATCH SIMPLE).
        builder.HasOne<FinancialPlannedTransaction>()
            .WithMany()
            .HasForeignKey(transaction => new { transaction.WorkspaceId, transaction.PlannedTransactionId })
            .HasPrincipalKey(planned => new { planned.WorkspaceId, planned.Id })
            .HasConstraintName("fk_financial_transactions_planned_same_family")
            .OnDelete(DeleteBehavior.Restrict);

        // No máximo UM lançamento ativo por previsão. Cancelados ficam fora do índice: o vínculo histórico é
        // preservado e a previsão pode ser realizada de novo.
        builder.HasIndex(transaction => transaction.PlannedTransactionId)
            .IsUnique()
            .HasFilter("planned_transaction_id IS NOT NULL AND NOT is_cancelled")
            .HasDatabaseName(ActiveRealizationIndex);

        builder.HasOne<ApplicationUser>()
            .WithMany()
            .HasForeignKey(transaction => transaction.CreatedByUserId)
            .OnDelete(DeleteBehavior.Restrict);
        builder.HasOne<ApplicationUser>()
            .WithMany()
            .HasForeignKey(transaction => transaction.UpdatedByUserId)
            .OnDelete(DeleteBehavior.Restrict);
        builder.HasOne<ApplicationUser>()
            .WithMany()
            .HasForeignKey(transaction => transaction.CancelledByUserId)
            .OnDelete(DeleteBehavior.Restrict);
    }
}
