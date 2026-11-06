using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using Saldo360.Domain.Budgets;
using Saldo360.Domain.Workspaces;

namespace Saldo360.Infrastructure.Persistence.Configurations;

internal sealed class FinancialBudgetRuleChangeConfiguration : IEntityTypeConfiguration<FinancialBudgetRuleChange>
{
    public const string UniqueRevisionIndex = "ux_budget_rule_changes_key_revision";

    public void Configure(EntityTypeBuilder<FinancialBudgetRuleChange> b)
    {
        b.ToTable("financial_budget_rule_changes", t =>
        {
            t.HasCheckConstraint("ck_budget_rule_competence", "EXTRACT(DAY FROM competence) = 1");
            t.HasCheckConstraint("ck_budget_rule_revision", "revision >= 1");
            t.HasCheckConstraint("ck_budget_rule_amount", "(action = 'Set' AND amount IS NOT NULL AND amount >= 0 AND scale(amount) <= 2) OR (action <> 'Set' AND amount IS NULL)");
            t.HasCheckConstraint("ck_budget_rule_category", "(target = 'Category') = (category_id IS NOT NULL)");
            t.HasCheckConstraint("ck_budget_rule_restore", "action <> 'Restore' OR scope = 'OnlyCompetence'");
        });
        b.HasKey(x => x.Id); b.Property(x => x.Id).ValueGeneratedNever();
        b.Property(x => x.Scope).HasConversion<string>().HasMaxLength(24);
        b.Property(x => x.Target).HasConversion<string>().HasMaxLength(24);
        b.Property(x => x.Action).HasConversion<string>().HasMaxLength(24);
        b.Property(x => x.Competence).HasColumnType("date");
        b.Property(x => x.Amount).HasColumnType("numeric");
        b.Property(x => x.Note).HasMaxLength(200);
        // Uma revisão por chave: duas alterações concorrentes calculam a mesma próxima revisão e só uma é gravada.
        b.HasIndex(x => new { x.WorkspaceId, x.Scope, x.Target, x.CategoryId, x.Competence, x.Revision }).IsUnique().AreNullsDistinct(false).HasDatabaseName(UniqueRevisionIndex);
        b.HasIndex(x => new { x.WorkspaceId, x.Competence });
        b.HasOne<FinancialWorkspace>().WithMany().HasForeignKey(x => x.WorkspaceId).OnDelete(DeleteBehavior.Restrict);
    }
}
