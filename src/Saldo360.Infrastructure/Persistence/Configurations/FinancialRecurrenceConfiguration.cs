using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using Saldo360.Domain.Accounts;
using Saldo360.Domain.Categories;
using Saldo360.Domain.Common;
using Saldo360.Domain.Workspaces;
using Saldo360.Domain.Recurrences;
using Saldo360.Domain.CreditCards;

namespace Saldo360.Infrastructure.Persistence.Configurations;
internal sealed class FinancialRecurrenceConfiguration : IEntityTypeConfiguration<FinancialRecurrence>
{
    public void Configure(EntityTypeBuilder<FinancialRecurrence> b)
    {
        b.ToTable("financial_recurrences", t=>{t.HasCheckConstraint("ck_recurrences_amount_positive","amount > 0");t.HasCheckConstraint("ck_recurrences_amount_scale",$"scale(amount) <= {Money.MaxScale}");t.HasCheckConstraint("ck_recurrences_interval","interval_value BETWEEN 1 AND 120");t.HasCheckConstraint("ck_recurrences_end","end_date IS NULL OR end_date >= start_date");t.HasCheckConstraint("ck_recurrences_count","occurrence_count IS NULL OR occurrence_count > 0");t.HasCheckConstraint("ck_recurrences_end_mode","NOT (end_date IS NOT NULL AND occurrence_count IS NOT NULL)");t.HasCheckConstraint("ck_recurrences_day","day_of_month BETWEEN 1 AND 31");});
        b.HasKey(x=>x.Id); b.Property(x=>x.Id).ValueGeneratedNever(); b.Property(x=>x.Type).HasConversion<string>().HasMaxLength(20); b.Property(x=>x.Frequency).HasConversion<string>().HasMaxLength(20); b.Property(x=>x.Status).HasConversion<string>().HasMaxLength(20); b.Property(x=>x.Description).HasMaxLength(FinancialRecurrence.DescriptionMaxLength).IsRequired(); b.Property(x=>x.Amount).HasColumnType("numeric"); b.Property(x=>x.Currency).HasMaxLength(3).IsFixedLength(); b.Property(x=>x.Interval).HasColumnName("interval_value"); b.Property(x=>x.StartDate).HasColumnType("date"); b.Property(x=>x.EndDate).HasColumnType("date");
        b.Property(x => x.Nature).HasColumnName("expense_nature").HasConversion<string>().HasMaxLength(20).HasDefaultValue(Saldo360.Domain.Categories.ExpenseNature.Undefined);
        b.ToTable(t => t.HasCheckConstraint("ck_recurrences_expense_nature", ExpenseNatureCheck.Sql("type", "expense_nature")));
        b.HasAlternateKey(x => new { x.WorkspaceId, x.Id });
        b.ToTable(t => {
            t.HasCheckConstraint("ck_recurrences_destination", "(financial_account_id IS NOT NULL) <> (financial_credit_card_id IS NOT NULL)");
            t.HasCheckConstraint("ck_recurrences_card_expense", "financial_credit_card_id IS NULL OR type = 'Expense'");
        });
        b.HasIndex(x=>new{x.WorkspaceId,x.Status,x.StartDate});
        b.HasOne<FinancialWorkspace>().WithMany().HasForeignKey(x=>x.WorkspaceId).OnDelete(DeleteBehavior.Restrict);
        b.HasOne<FinancialAccount>().WithMany().HasForeignKey(x=>new{x.WorkspaceId,AccountId=x.FinancialAccountId}).HasPrincipalKey(x=>new{x.WorkspaceId,x.Id}).OnDelete(DeleteBehavior.Restrict);
        b.HasOne<FinancialCreditCard>().WithMany().HasForeignKey(x=>new{x.WorkspaceId,CardId=x.FinancialCreditCardId}).HasPrincipalKey(x=>new{x.WorkspaceId,x.Id}).OnDelete(DeleteBehavior.Restrict);
        b.HasOne<FinancialCategory>().WithMany().HasForeignKey(x=>new{x.WorkspaceId,CategoryId=x.FinancialCategoryId,x.Type}).HasPrincipalKey(x=>new{x.WorkspaceId,x.Id,x.Type}).OnDelete(DeleteBehavior.Restrict);
    }
}
