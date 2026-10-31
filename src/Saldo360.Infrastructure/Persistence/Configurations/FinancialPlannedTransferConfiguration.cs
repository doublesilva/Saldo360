using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using Saldo360.Domain.Accounts;
using Saldo360.Domain.Common;
using Saldo360.Domain.Transfers;
using Saldo360.Domain.Users;
using Saldo360.Domain.Workspaces;

namespace Saldo360.Infrastructure.Persistence.Configurations;

internal sealed class FinancialPlannedTransferConfiguration : IEntityTypeConfiguration<FinancialPlannedTransfer>
{
    public void Configure(EntityTypeBuilder<FinancialPlannedTransfer> builder)
    {
        builder.ToTable("financial_planned_transfers", table =>
        {
            table.HasCheckConstraint("ck_financial_planned_transfers_distinct_accounts", "source_account_id <> destination_account_id");
            table.HasCheckConstraint("ck_financial_planned_transfers_currency", $"currency = '{FinancialPlannedTransfer.DefaultCurrency}'");
            table.HasCheckConstraint("ck_financial_planned_transfers_description_not_blank", "btrim(description) <> ''");
            table.HasCheckConstraint("ck_financial_planned_transfers_amount_positive", "amount > 0");
            table.HasCheckConstraint("ck_financial_planned_transfers_amount_scale", $"scale(amount) <= {Money.MaxScale}");
            table.HasCheckConstraint("ck_financial_planned_transfers_amount_range", $"amount < {Money.Limit}");
            table.HasCheckConstraint("ck_financial_planned_transfers_cancelled_at", "is_cancelled = (cancelled_at IS NOT NULL)");
            // Cancelada e realizada são estados excludentes; a associação carrega origem e data.
            table.HasCheckConstraint("ck_financial_planned_transfers_state", "NOT (is_cancelled AND realized_transfer_id IS NOT NULL)");
            table.HasCheckConstraint("ck_financial_planned_transfers_link", "(realized_transfer_id IS NULL) = (link_origin IS NULL) AND (realized_transfer_id IS NULL) = (linked_at IS NULL)");
        });

        builder.HasKey(item => item.Id);
        builder.Property(item => item.Id).ValueGeneratedNever();
        builder.Property(item => item.Amount).HasColumnType("numeric");
        builder.Property(item => item.DueDate).HasColumnType("date");
        builder.Property(item => item.Description).HasMaxLength(FinancialPlannedTransfer.DescriptionMaxLength).IsRequired();
        builder.Property(item => item.Currency).HasMaxLength(3).IsFixedLength();
        builder.Property(item => item.LinkOrigin).HasConversion<string>().HasMaxLength(16);

        builder.HasIndex(item => new { item.WorkspaceId, item.DueDate, item.Id });
        // Uma transferência realizada cumpre no máximo uma previsão ativa: nada de associação duplicada.
        builder.HasIndex(item => item.RealizedTransferId).IsUnique().HasFilter("realized_transfer_id IS NOT NULL");

        builder.HasOne<FinancialWorkspace>().WithMany().HasForeignKey(item => item.WorkspaceId).OnDelete(DeleteBehavior.Restrict);
        builder.HasOne<FinancialAccount>().WithMany().HasForeignKey(item => new { item.WorkspaceId, item.SourceAccountId })
            .HasPrincipalKey(account => new { account.WorkspaceId, account.Id }).HasConstraintName("fk_financial_planned_transfers_source_account_same_family").OnDelete(DeleteBehavior.Restrict);
        builder.HasOne<FinancialAccount>().WithMany().HasForeignKey(item => new { item.WorkspaceId, item.DestinationAccountId })
            .HasPrincipalKey(account => new { account.WorkspaceId, account.Id }).HasConstraintName("fk_financial_planned_transfers_destination_account_same_family").OnDelete(DeleteBehavior.Restrict);
        builder.HasOne<FinancialTransfer>().WithMany().HasForeignKey(item => item.RealizedTransferId).OnDelete(DeleteBehavior.Restrict);
        builder.HasOne<ApplicationUser>().WithMany().HasForeignKey(item => item.CreatedByUserId).OnDelete(DeleteBehavior.Restrict);
        builder.HasOne<ApplicationUser>().WithMany().HasForeignKey(item => item.CancelledByUserId).OnDelete(DeleteBehavior.Restrict);
    }
}
