using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using Saldo360.Domain.Transactions;
using Saldo360.Domain.Users;
using Saldo360.Domain.Workspaces;

namespace Saldo360.Infrastructure.Persistence.Configurations;

internal sealed class FinancialBankRefundConfiguration : IEntityTypeConfiguration<FinancialBankRefund>
{
    public void Configure(EntityTypeBuilder<FinancialBankRefund> b)
    {
        b.ToTable("financial_bank_refunds", t =>
        {
            t.HasCheckConstraint("ck_financial_bank_refunds_amount_positive", "amount > 0");
            t.HasCheckConstraint("ck_financial_bank_refunds_distinct", "refund_transaction_id <> original_transaction_id");
            t.HasCheckConstraint("ck_financial_bank_refunds_cancelled_pair", "(cancelled_at IS NULL) = (cancelled_by_user_id IS NULL)");
        });
        b.HasKey(x => x.Id); b.Property(x => x.Id).ValueGeneratedNever();
        b.Property(x => x.Amount).HasColumnType("numeric");
        b.Ignore(x => x.IsActive);
        // Um crédito devolve no máximo UMA despesa ativa ao mesmo tempo (o histórico cancelado fica fora do índice).
        b.HasIndex(x => x.RefundTransactionId).IsUnique().HasFilter("cancelled_at IS NULL").HasDatabaseName("ux_financial_bank_refunds_active_refund");
        b.HasIndex(x => new { x.WorkspaceId, x.OriginalTransactionId });
        // Os dois lançamentos pertencem à MESMA família do vínculo (nenhum vínculo entre orçamentos).
        b.HasOne<FinancialTransaction>().WithMany().HasForeignKey(x => new { x.WorkspaceId, x.RefundTransactionId }).HasPrincipalKey(t => new { t.WorkspaceId, t.Id })
            .HasConstraintName("fk_financial_bank_refunds_refund_same_workspace").OnDelete(DeleteBehavior.Restrict);
        b.HasOne<FinancialTransaction>().WithMany().HasForeignKey(x => new { x.WorkspaceId, x.OriginalTransactionId }).HasPrincipalKey(t => new { t.WorkspaceId, t.Id })
            .HasConstraintName("fk_financial_bank_refunds_original_same_workspace").OnDelete(DeleteBehavior.Restrict);
        b.HasOne<FinancialWorkspace>().WithMany().HasForeignKey(x => x.WorkspaceId).OnDelete(DeleteBehavior.Restrict);
        b.HasOne<ApplicationUser>().WithMany().HasForeignKey(x => x.ConfirmedByUserId).OnDelete(DeleteBehavior.Restrict);
        b.HasOne<ApplicationUser>().WithMany().HasForeignKey(x => x.CancelledByUserId).OnDelete(DeleteBehavior.Restrict);
    }
}
