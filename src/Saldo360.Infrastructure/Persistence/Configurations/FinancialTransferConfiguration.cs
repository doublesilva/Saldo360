using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using Saldo360.Domain.Accounts;
using Saldo360.Domain.Common;
using Saldo360.Domain.Families;
using Saldo360.Domain.Transfers;
using Saldo360.Domain.Users;

namespace Saldo360.Infrastructure.Persistence.Configurations;

internal sealed class FinancialTransferConfiguration : IEntityTypeConfiguration<FinancialTransfer>
{
    public void Configure(EntityTypeBuilder<FinancialTransfer> builder)
    {
        builder.ToTable("financial_transfers", table =>
        {
            table.HasCheckConstraint("ck_financial_transfers_distinct_accounts", "source_account_id <> destination_account_id");
            table.HasCheckConstraint("ck_financial_transfers_currency", $"currency = '{FinancialTransfer.DefaultCurrency}'");
            table.HasCheckConstraint("ck_financial_transfers_description_not_blank", "btrim(description) <> ''");
            table.HasCheckConstraint("ck_financial_transfers_description_trimmed",
                "octet_length(description) = octet_length(btrim(description))");

            // Mesmas regras Money dos lançamentos: positivo, até 2 casas (numeric sem escala fixa, sem arredondar).
            table.HasCheckConstraint("ck_financial_transfers_amount_positive", "amount > 0");
            table.HasCheckConstraint("ck_financial_transfers_amount_scale", $"scale(amount) <= {Money.MaxScale}");
            table.HasCheckConstraint("ck_financial_transfers_amount_range", $"amount < {Money.Limit}");

            table.HasCheckConstraint("ck_financial_transfers_cancelled_at", "is_cancelled = (cancelled_at IS NOT NULL)");
            table.HasCheckConstraint("ck_financial_transfers_cancelled_pair", "(cancelled_at IS NULL) = (cancelled_by_user_id IS NULL)");
        });

        builder.HasKey(transfer => transfer.Id);
        builder.Property(transfer => transfer.Id).ValueGeneratedNever();
        builder.Property(transfer => transfer.Amount).HasColumnType("numeric");
        builder.Property(transfer => transfer.TransferDate).HasColumnType("date");
        builder.Property(transfer => transfer.Description).HasMaxLength(FinancialTransfer.DescriptionMaxLength).IsRequired();
        builder.Property(transfer => transfer.Currency).HasMaxLength(3).IsFixedLength();

        // Listagem: data decrescente e Id decrescente (ordem estável), sempre filtrada por família.
        builder.HasIndex(transfer => new { transfer.FamilyId, transfer.TransferDate, transfer.Id })
            .IsDescending(false, true, true);

        // Restrict em todas as FKs: nada apaga transferências por cascata (histórico preservado).
        builder.HasOne<Family>()
            .WithMany()
            .HasForeignKey(transfer => transfer.FamilyId)
            .OnDelete(DeleteBehavior.Restrict);

        // Origem e destino: (family_id, conta) → financial_accounts (family_id, id), as duas da MESMA família.
        builder.HasOne<FinancialAccount>()
            .WithMany()
            .HasForeignKey(transfer => new { transfer.FamilyId, transfer.SourceAccountId })
            .HasPrincipalKey(account => new { account.FamilyId, account.Id })
            .HasConstraintName("fk_financial_transfers_source_account_same_family")
            .OnDelete(DeleteBehavior.Restrict);
        builder.HasOne<FinancialAccount>()
            .WithMany()
            .HasForeignKey(transfer => new { transfer.FamilyId, transfer.DestinationAccountId })
            .HasPrincipalKey(account => new { account.FamilyId, account.Id })
            .HasConstraintName("fk_financial_transfers_destination_account_same_family")
            .OnDelete(DeleteBehavior.Restrict);

        builder.HasOne<ApplicationUser>()
            .WithMany()
            .HasForeignKey(transfer => transfer.CreatedByUserId)
            .OnDelete(DeleteBehavior.Restrict);
        builder.HasOne<ApplicationUser>()
            .WithMany()
            .HasForeignKey(transfer => transfer.CancelledByUserId)
            .OnDelete(DeleteBehavior.Restrict);
    }
}
