using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using Saldo360.Domain.Accounts;
using Saldo360.Domain.CreditCards;
using Saldo360.Domain.Families;
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
        builder.Property(card => card.CreditLimit).HasColumnType("numeric");
        builder.HasIndex(card => new { card.FamilyId, card.IsArchived });
        builder.HasAlternateKey(card => new { card.FamilyId, card.Id });
        builder.HasOne<Family>().WithMany().HasForeignKey(card => card.FamilyId).OnDelete(DeleteBehavior.Restrict);
        builder.HasOne<ApplicationUser>().WithMany().HasForeignKey(card => card.CreatedByUserId).OnDelete(DeleteBehavior.Restrict);
        builder.HasOne<ApplicationUser>().WithMany().HasForeignKey(card => card.UpdatedByUserId).OnDelete(DeleteBehavior.Restrict);
        builder.HasOne<ApplicationUser>().WithMany().HasForeignKey(card => card.ArchivedByUserId).OnDelete(DeleteBehavior.Restrict);
        builder.HasOne<FinancialAccount>().WithMany().HasForeignKey(card => new { card.FamilyId, card.DefaultPaymentAccountId })
            .HasPrincipalKey(account => new { account.FamilyId, account.Id }).OnDelete(DeleteBehavior.Restrict)
            .HasConstraintName("fk_financial_credit_cards_family_accounts");
        builder.HasOne<FamilyMember>().WithMany().HasForeignKey(card => new { card.FamilyId, card.HolderUserId })
            .HasPrincipalKey(member => new { member.FamilyId, member.UserId }).OnDelete(DeleteBehavior.Restrict)
            .HasConstraintName("fk_financial_credit_cards_family_members_holder");
    }
}
