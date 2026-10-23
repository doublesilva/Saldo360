using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using Saldo360.Domain.Accounts;
using Saldo360.Domain.CreditCards;
using Saldo360.Domain.Workspaces;
using Saldo360.Domain.Users;

namespace Saldo360.Infrastructure.Persistence.Configurations;

internal sealed class FinancialCreditCardConfiguration : IEntityTypeConfiguration<FinancialCreditCard>
{
    public void Configure(EntityTypeBuilder<FinancialCreditCard> builder)
    {
        builder.ToTable("financial_credit_cards", table =>
        {
            table.HasCheckConstraint("ck_financial_credit_cards_name_not_blank", "btrim(name) <> ''");
            table.HasCheckConstraint("ck_financial_credit_cards_issuer_not_blank", "btrim(issuer) <> ''");
            table.HasCheckConstraint("ck_financial_credit_cards_last_four_digits", "last_four_digits IS NULL OR last_four_digits ~ '^[0-9]{4}$'");
            table.HasCheckConstraint("ck_financial_credit_cards_credit_limit_non_negative", "credit_limit >= 0");
            table.HasCheckConstraint("ck_financial_credit_cards_credit_limit_scale", "scale(credit_limit) <= 2");
            table.HasCheckConstraint("ck_financial_credit_cards_closing_day", "closing_day BETWEEN 1 AND 31");
            table.HasCheckConstraint("ck_financial_credit_cards_due_day", "due_day BETWEEN 1 AND 31");
            table.HasCheckConstraint("ck_financial_credit_cards_archived", "is_archived = (archived_at IS NOT NULL) AND (archived_at IS NULL) = (archived_by_user_id IS NULL)");
        });
        builder.HasKey(card => card.Id);
        builder.Property(card => card.Id).ValueGeneratedNever();
        builder.Property(card => card.Name).HasMaxLength(FinancialCreditCard.NameMaxLength).IsRequired();
        builder.Property(card => card.Issuer).HasMaxLength(FinancialCreditCard.IssuerMaxLength).IsRequired();
        builder.Property(card => card.LastFourDigits).HasMaxLength(FinancialCreditCard.LastFourDigitsLength);
        builder.Property(card => card.GmailQuery).HasMaxLength(2048);
        builder.Property(card => card.CreditLimit).HasColumnType("numeric");
        builder.HasIndex(card => new { card.WorkspaceId, card.IsArchived });
        builder.HasAlternateKey(card => new { card.WorkspaceId, card.Id });
        builder.HasOne<FinancialWorkspace>().WithMany().HasForeignKey(card => card.WorkspaceId).OnDelete(DeleteBehavior.Restrict);
        builder.HasOne<ApplicationUser>().WithMany().HasForeignKey(card => card.CreatedByUserId).OnDelete(DeleteBehavior.Restrict);
        builder.HasOne<ApplicationUser>().WithMany().HasForeignKey(card => card.UpdatedByUserId).OnDelete(DeleteBehavior.Restrict);
        builder.HasOne<ApplicationUser>().WithMany().HasForeignKey(card => card.ArchivedByUserId).OnDelete(DeleteBehavior.Restrict);
        builder.HasOne<FinancialAccount>().WithMany().HasForeignKey(card => new { card.WorkspaceId, card.DefaultPaymentAccountId })
            .HasPrincipalKey(account => new { account.WorkspaceId, account.Id }).OnDelete(DeleteBehavior.Restrict)
            .HasConstraintName("fk_financial_credit_cards_family_accounts");
        builder.HasOne<FinancialWorkspace>().WithMany().HasForeignKey(card => new { card.WorkspaceId, card.HolderUserId })
            .HasPrincipalKey(workspace => new { workspace.Id, workspace.OwnerUserId }).OnDelete(DeleteBehavior.Restrict)
            .HasConstraintName("fk_financial_credit_cards_workspace_holder");
    }
}
