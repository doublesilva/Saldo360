using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using Saldo360.Domain.Accounts;
using Saldo360.Domain.Categories;
using Saldo360.Domain.CreditCards;
using Saldo360.Domain.Pluggy;
using Saldo360.Domain.Transactions;
using Saldo360.Domain.Transfers;
using Saldo360.Domain.Users;
using Saldo360.Domain.Workspaces;

namespace Saldo360.Infrastructure.Persistence.Configurations;

internal sealed class PluggyAccountLinkConfiguration : IEntityTypeConfiguration<PluggyAccountLink>
{
    public void Configure(EntityTypeBuilder<PluggyAccountLink> b)
    {
        b.ToTable("pluggy_account_links", t =>
        {
            t.HasCheckConstraint("ck_pluggy_account_links_kind", "kind IN ('Bank','Card')");
            t.HasCheckConstraint("ck_pluggy_account_links_status", "status IN ('Active','ConsentExpired','Attention')");
            // Conta bancária XOR cartão, conforme o tipo.
            t.HasCheckConstraint("ck_pluggy_account_links_target",
                "(kind = 'Bank' AND financial_account_id IS NOT NULL AND credit_card_id IS NULL) OR (kind = 'Card' AND credit_card_id IS NOT NULL AND financial_account_id IS NULL)");
        });
        b.HasKey(x => x.Id); b.Property(x => x.Id).ValueGeneratedNever();
        b.Property(x => x.PluggyAccountId).HasMaxLength(64).IsRequired();
        b.Property(x => x.Kind).HasConversion<string>().HasMaxLength(8);
        b.Property(x => x.Status).HasConversion<string>().HasMaxLength(16);
        b.Property(x => x.DisplayName).HasMaxLength(PluggyAccountLink.NameMaxLength).IsRequired();
        b.Property(x => x.InstitutionName).HasMaxLength(120);
        b.Property(x => x.PluggyType).HasMaxLength(32);
        b.Property(x => x.PluggySubtype).HasMaxLength(40);
        b.Property(x => x.ImportFrom).HasColumnType("date");
        b.Property(x => x.ObservedBalance).HasPrecision(19, 4);
        b.Property(x => x.OverdraftContractedLimit).HasPrecision(19, 4);
        b.Property(x => x.OverdraftUsedLimit).HasPrecision(19, 4);
        b.Property(x => x.CreditLimit).HasPrecision(19, 4);
        b.Property(x => x.AvailableCreditLimit).HasPrecision(19, 4);

        // Uma conta externa só pode ser vinculada uma vez; e cada conta/cartão do Saldo360 tem no máximo uma conta externa.
        b.HasIndex(x => new { x.ConnectionId, x.PluggyAccountId }).IsUnique().HasDatabaseName("ux_pluggy_account_links_external");
        b.HasIndex(x => x.FinancialAccountId).IsUnique().HasFilter("financial_account_id IS NOT NULL").HasDatabaseName("ux_pluggy_account_links_financial_account");
        b.HasIndex(x => x.CreditCardId).IsUnique().HasFilter("credit_card_id IS NOT NULL").HasDatabaseName("ux_pluggy_account_links_credit_card");
        b.HasIndex(x => new { x.WorkspaceId, x.ConnectionId });

        b.HasOne<FinancialWorkspace>().WithMany().HasForeignKey(x => x.WorkspaceId).OnDelete(DeleteBehavior.Restrict);
        b.HasOne<PluggyItemLink>().WithMany().HasForeignKey(x => x.ConnectionId).OnDelete(DeleteBehavior.Restrict);
        b.HasOne<FinancialAccount>().WithMany().HasForeignKey(x => new { x.WorkspaceId, x.FinancialAccountId }).HasPrincipalKey(a => new { a.WorkspaceId, a.Id })
            .HasConstraintName("fk_pluggy_account_links_accounts_same_workspace").OnDelete(DeleteBehavior.Restrict);
        b.HasOne<FinancialCreditCard>().WithMany().HasForeignKey(x => new { x.WorkspaceId, x.CreditCardId }).HasPrincipalKey(c => new { c.WorkspaceId, c.Id })
            .HasConstraintName("fk_pluggy_account_links_cards_same_workspace").OnDelete(DeleteBehavior.Restrict);
        b.HasOne<ApplicationUser>().WithMany().HasForeignKey(x => x.LinkedByUserId).OnDelete(DeleteBehavior.Restrict);
    }
}

internal sealed class PluggyPostingConfiguration : IEntityTypeConfiguration<PluggyPosting>
{
    public void Configure(EntityTypeBuilder<PluggyPosting> b)
    {
        b.ToTable("pluggy_postings", t =>
        {
            t.HasCheckConstraint("ck_pluggy_postings_state", "state IN ('WaitingPosted','Applied','NeedsReview','Covered','Ignored')");
            t.HasCheckConstraint("ck_pluggy_postings_kind", "kind IN ('None','BankMovement','Transfer','CardPurchase','CardBillPayment','CardCredit')");
            t.HasCheckConstraint("ck_pluggy_postings_resolution", "resolution IS NULL OR resolution IN ('AsIncome','AsExpense','AsCardPurchase','Ignore')");
            t.HasCheckConstraint("ck_pluggy_postings_category_source", "category_source IN ('None','Mapped','User','Pluggy','Rule','AI')");
            t.HasCheckConstraint("ck_pluggy_postings_link_origin", "link_origin IS NULL OR (link_origin IN ('Inferred','UserConfirmed') AND kind = 'Transfer' AND state = 'Applied')");
            // Aplicado sempre aponta para o registro que gerou; qualquer outro estado não aponta para movimento próprio.
            t.HasCheckConstraint("ck_pluggy_postings_applied_targets",
                "(state = 'Applied' AND ((kind = 'BankMovement' AND financial_transaction_id IS NOT NULL AND credit_card_invoice_adjustment_id IS NULL) OR (kind = 'Transfer' AND financial_transfer_id IS NOT NULL AND credit_card_invoice_adjustment_id IS NULL) "
                + "OR (kind = 'CardPurchase' AND credit_card_purchase_id IS NOT NULL AND credit_card_invoice_adjustment_id IS NULL) "
                + "OR (kind = 'CardBillPayment' AND financial_transaction_id IS NOT NULL AND credit_card_invoice_payment_id IS NOT NULL AND credit_card_invoice_adjustment_id IS NULL) "
                + "OR (kind = 'CardCredit' AND credit_card_invoice_adjustment_id IS NOT NULL AND financial_transaction_id IS NULL AND financial_transfer_id IS NULL AND credit_card_purchase_id IS NULL AND credit_card_invoice_payment_id IS NULL))) "
                + "OR (state <> 'Applied' AND financial_transaction_id IS NULL AND financial_transfer_id IS NULL AND credit_card_purchase_id IS NULL AND credit_card_invoice_payment_id IS NULL AND credit_card_invoice_adjustment_id IS NULL)");
            t.HasCheckConstraint("ck_pluggy_postings_applied_snapshot", "(state = 'Applied') = (applied_amount IS NOT NULL AND applied_date IS NOT NULL)");
        });
        b.HasKey(x => x.Id); b.Property(x => x.Id).ValueGeneratedNever();
        b.Property(x => x.State).HasConversion<string>().HasMaxLength(16);
        b.Property(x => x.Kind).HasConversion<string>().HasMaxLength(20);
        b.Property(x => x.Resolution).HasConversion<string>().HasMaxLength(16);
        b.Property(x => x.CategorySource).HasConversion<string>().HasMaxLength(8);
        b.Property(x => x.AppliedType).HasConversion<string>().HasMaxLength(20);
        b.Property(x => x.LinkOrigin).HasConversion<string>().HasMaxLength(16);
        b.Property(x => x.LinkRuleVersion).HasMaxLength(PluggyPosting.LinkRuleVersionMaxLength);
        b.Property(x => x.LinkReviewReason).HasMaxLength(PluggyPosting.ReasonMaxLength);
        b.Property(x => x.Reason).HasMaxLength(PluggyPosting.ReasonMaxLength);
        b.Property(x => x.CategoryMethodVersion).HasMaxLength(PluggyPosting.CategoryMethodMaxLength);
        b.Property(x => x.CategoryReason).HasMaxLength(PluggyPosting.CategoryReasonMaxLength);
        b.Property(x => x.AppliedAmount).HasColumnType("numeric");
        b.Property(x => x.AppliedDate).HasColumnType("date");

        // Idempotência: uma transação importada nunca gera dois registros. E um registro do núcleo pertence a um único rastro.
        b.HasIndex(x => x.ImportedTransactionId).IsUnique().HasDatabaseName("ux_pluggy_postings_imported");
        b.HasIndex(x => x.FinancialTransactionId).IsUnique().HasFilter("financial_transaction_id IS NOT NULL").HasDatabaseName("ux_pluggy_postings_transaction");
        b.HasIndex(x => x.CreditCardPurchaseId).IsUnique().HasFilter("credit_card_purchase_id IS NOT NULL").HasDatabaseName("ux_pluggy_postings_purchase");
        b.HasIndex(x => x.CreditCardInvoicePaymentId).IsUnique().HasFilter("credit_card_invoice_payment_id IS NOT NULL").HasDatabaseName("ux_pluggy_postings_invoice_payment");
        b.HasIndex(x => x.CreditCardInvoiceAdjustmentId).IsUnique().HasFilter("credit_card_invoice_adjustment_id IS NOT NULL").HasDatabaseName("ux_pluggy_postings_invoice_adjustment");
        b.HasIndex(x => new { x.WorkspaceId, x.State });
        b.HasIndex(x => new { x.AccountLinkId, x.State });

        b.HasOne<FinancialWorkspace>().WithMany().HasForeignKey(x => x.WorkspaceId).OnDelete(DeleteBehavior.Restrict);
        b.HasOne<PluggyImportedTransaction>().WithMany().HasForeignKey(x => x.ImportedTransactionId).OnDelete(DeleteBehavior.Restrict);
        b.HasOne<PluggyAccountLink>().WithMany().HasForeignKey(x => x.AccountLinkId).OnDelete(DeleteBehavior.Restrict);
        b.HasOne<FinancialTransaction>().WithMany().HasForeignKey(x => new { x.WorkspaceId, x.FinancialTransactionId }).HasPrincipalKey(t => new { t.WorkspaceId, t.Id })
            .HasConstraintName("fk_pluggy_postings_transactions_same_workspace").OnDelete(DeleteBehavior.Restrict);
        b.HasOne<FinancialTransfer>().WithMany().HasForeignKey(x => x.FinancialTransferId).OnDelete(DeleteBehavior.Restrict);
        b.HasOne<FinancialCreditCardPurchase>().WithMany().HasForeignKey(x => new { x.WorkspaceId, x.CreditCardPurchaseId }).HasPrincipalKey(p => new { p.WorkspaceId, p.Id })
            .HasConstraintName("fk_pluggy_postings_purchases_same_workspace").OnDelete(DeleteBehavior.Restrict);
        b.HasOne<FinancialCreditCardInvoicePayment>().WithMany().HasForeignKey(x => x.CreditCardInvoicePaymentId).OnDelete(DeleteBehavior.Restrict);
        b.HasOne<FinancialCreditCardInvoiceAdjustment>().WithMany().HasForeignKey(x => x.CreditCardInvoiceAdjustmentId).OnDelete(DeleteBehavior.Restrict);
        b.HasOne<PluggyPosting>().WithMany().HasForeignKey(x => x.CounterpartPostingId).OnDelete(DeleteBehavior.Restrict);
        b.HasOne<FinancialCategory>().WithMany().HasForeignKey(x => x.CategoryId).OnDelete(DeleteBehavior.Restrict);
        b.HasOne<ApplicationUser>().WithMany().HasForeignKey(x => x.ResolvedByUserId).OnDelete(DeleteBehavior.Restrict);
    }
}

internal sealed class PluggyTransferRejectionConfiguration : IEntityTypeConfiguration<PluggyTransferRejection>
{
    public void Configure(EntityTypeBuilder<PluggyTransferRejection> b)
    {
        b.ToTable("pluggy_transfer_rejections");
        b.HasKey(x => x.Id); b.Property(x => x.Id).ValueGeneratedNever();
        b.HasIndex(x => new { x.FirstPostingId, x.SecondPostingId }).IsUnique().HasDatabaseName("ux_pluggy_transfer_rejections_pair");
        b.HasIndex(x => x.WorkspaceId);
        b.HasOne<FinancialWorkspace>().WithMany().HasForeignKey(x => x.WorkspaceId).OnDelete(DeleteBehavior.Restrict);
        b.HasOne<PluggyPosting>().WithMany().HasForeignKey(x => x.FirstPostingId).OnDelete(DeleteBehavior.Restrict);
        b.HasOne<PluggyPosting>().WithMany().HasForeignKey(x => x.SecondPostingId).OnDelete(DeleteBehavior.Restrict);
        b.HasOne<ApplicationUser>().WithMany().HasForeignKey(x => x.RejectedByUserId).OnDelete(DeleteBehavior.Restrict);
    }
}

internal sealed class PluggyCardBillConfiguration : IEntityTypeConfiguration<PluggyCardBill>
{
    public void Configure(EntityTypeBuilder<PluggyCardBill> b)
    {
        b.ToTable("pluggy_card_bills", t => t.HasCheckConstraint("ck_pluggy_card_bills_reconciliation", "reconciliation IN ('NotVerified','Reconciled','Divergent','Incomplete')"));
        b.HasKey(x => x.Id); b.Property(x => x.Id).ValueGeneratedNever();
        b.Property(x => x.PluggyBillId).HasMaxLength(80).IsRequired();
        b.Property(x => x.ClosingDate).HasColumnType("date");
        b.Property(x => x.DueDate).HasColumnType("date");
        b.Property(x => x.TotalAmount).HasPrecision(19, 4);
        b.Property(x => x.Currency).HasMaxLength(8);
        b.Property(x => x.FinanceChargesTotal).HasPrecision(19, 4);
        b.Property(x => x.MinimumPayment).HasPrecision(19, 4);
        b.Property(x => x.ComponentsTotal).HasPrecision(19, 4);
        b.Property(x => x.Reconciliation).HasConversion<string>().HasMaxLength(16);
        b.Property(x => x.ReconciliationReason).HasMaxLength(40);
        b.HasIndex(x => new { x.AccountLinkId, x.PluggyBillId }).IsUnique().HasDatabaseName("ux_pluggy_card_bills_external");
        b.HasIndex(x => new { x.AccountLinkId, x.ClosingDate });
        b.HasOne<FinancialWorkspace>().WithMany().HasForeignKey(x => x.WorkspaceId).OnDelete(DeleteBehavior.Restrict);
        b.HasOne<PluggyAccountLink>().WithMany().HasForeignKey(x => x.AccountLinkId).OnDelete(DeleteBehavior.Restrict);
    }
}


internal sealed class PluggyCardBillPaymentConfiguration : IEntityTypeConfiguration<PluggyCardBillPayment>
{
    public void Configure(EntityTypeBuilder<PluggyCardBillPayment> b)
    {
        b.ToTable("pluggy_card_bill_payments", t => t.HasCheckConstraint("ck_pluggy_card_bill_payments_amount", "amount > 0"));
        b.HasKey(x => x.Id); b.Property(x => x.Id).ValueGeneratedNever();
        b.Property(x => x.Amount).HasPrecision(19, 4);
        b.Property(x => x.PaymentDate).HasColumnType("date");
        b.Property(x => x.PaymentMode).HasMaxLength(24);
        b.Property(x => x.ValueType).HasMaxLength(24);
        b.HasIndex(x => new { x.BillId, x.Ordinal }).IsUnique().HasDatabaseName("ux_pluggy_card_bill_payments_ordinal");
        b.HasIndex(x => x.WorkspaceId);
        b.HasOne<FinancialWorkspace>().WithMany().HasForeignKey(x => x.WorkspaceId).OnDelete(DeleteBehavior.Restrict);
        b.HasOne<PluggyCardBill>().WithMany().HasForeignKey(x => x.BillId).OnDelete(DeleteBehavior.Cascade);
    }
}

internal sealed class FinancialAccountBalanceAnchorConfiguration : IEntityTypeConfiguration<Saldo360.Domain.Accounts.FinancialAccountBalanceAnchor>
{
    public void Configure(EntityTypeBuilder<Saldo360.Domain.Accounts.FinancialAccountBalanceAnchor> b)
    {
        b.ToTable("financial_account_balance_anchors", t =>
        {
            t.HasCheckConstraint("ck_balance_anchors_kind", "kind IN ('CurrentBalance','AvailableBalance','Unknown')");
            t.HasCheckConstraint("ck_balance_anchors_reliability", "reliability IN ('Reliable','Provisional','Unreliable')");
            // Derivação utilizável sempre traz valor e data; indisponível nunca traz.
            t.HasCheckConstraint("ck_balance_anchors_derivation", "(derived_opening_amount IS NULL) = (derived_opening_date IS NULL) AND (reliability <> 'Unreliable' OR derived_opening_amount IS NULL)");
        });
        b.HasKey(x => x.Id); b.Property(x => x.Id).ValueGeneratedNever();
        b.Property(x => x.Amount).HasPrecision(19, 4);
        b.Property(x => x.DerivedOpeningAmount).HasPrecision(19, 4);
        b.Property(x => x.AsOfDate).HasColumnType("date");
        b.Property(x => x.DerivedOpeningDate).HasColumnType("date");
        b.Property(x => x.Kind).HasConversion<string>().HasMaxLength(16);
        b.Property(x => x.Reliability).HasConversion<string>().HasMaxLength(12);
        b.Property(x => x.Source).HasMaxLength(Saldo360.Domain.Accounts.FinancialAccountBalanceAnchor.SourceMaxLength).IsRequired();
        b.Property(x => x.UnavailableReason).HasMaxLength(Saldo360.Domain.Accounts.FinancialAccountBalanceAnchor.ReasonMaxLength);
        // Uma referência vigente por conta; o histórico das anteriores é mantido.
        b.HasIndex(x => x.AccountId).IsUnique().HasFilter("is_current").HasDatabaseName("ux_balance_anchors_current");
        b.HasIndex(x => new { x.WorkspaceId, x.AccountId, x.ObservedAt });
        b.HasOne<FinancialWorkspace>().WithMany().HasForeignKey(x => x.WorkspaceId).OnDelete(DeleteBehavior.Restrict);
        b.HasOne<Saldo360.Domain.Accounts.FinancialAccount>().WithMany().HasForeignKey(x => x.AccountId).OnDelete(DeleteBehavior.Restrict);
    }
}
