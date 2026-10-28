using System.Security.Cryptography;
using System.Text;
using Microsoft.EntityFrameworkCore;
using Saldo360.Application.Pluggy;
using Saldo360.Domain.Categories;
using Saldo360.Domain.CreditCards;
using Saldo360.Domain.Pluggy;
using Saldo360.Domain.Transactions;
using Saldo360.Domain.Transfers;
using Saldo360.Infrastructure.CreditCards;
using Saldo360.Infrastructure.Persistence;
using Saldo360.Domain.Common;

namespace Saldo360.Infrastructure.Pluggy;

/// <summary>
/// Transforma as transações importadas (T09.2) em movimentos do núcleo financeiro, sem criar um segundo núcleo: receitas/despesas viram
/// <see cref="FinancialTransaction"/>, transferências entre contas integradas viram <see cref="FinancialTransfer"/>, compras de cartão
/// viram compras de uma parcela na fatura do cartão e o pagamento de fatura vira o pagamento de fatura já existente. Cada transação
/// importada tem no máximo um <see cref="PluggyPosting"/>, que guarda o que ela gerou: reprocessar é idempotente. Roda serializado por
/// orçamento (trava na linha do orçamento) e numa única transação do banco. As regras de decisão ficam em
/// <see cref="PluggyFinancialMapper"/> e <see cref="PluggyPairing"/> (puras).
/// </summary>
internal sealed class PluggyPostingEngine(Saldo360DbContext db) : IPluggyPostingEngine
{
    private const int TransferToleranceDays = 1;
    private const int BillPaymentToleranceDays = 3;
    private const string FallbackCategoryName = "Outros";

    public async Task<PluggyApplySummary> ApplyAsync(Guid workspaceId, Guid userId, DateTimeOffset now, CancellationToken ct)
    {
        var ownsTransaction = db.Database.CurrentTransaction is null;
        var tx = ownsTransaction ? await db.Database.BeginTransactionAsync(ct) : null;
        try
        {
            await db.Database.ExecuteSqlInterpolatedAsync($"SELECT id FROM financial_workspaces WHERE id = {workspaceId} FOR UPDATE", ct);
            var summary = await new Run(db, workspaceId, userId, now).ExecuteAsync(ct);
            if (tx is not null) await tx.CommitAsync(ct);
            return summary;
        }
        catch
        {
            if (tx is not null) await tx.RollbackAsync(CancellationToken.None);
            db.ChangeTracker.Clear();
            throw;
        }
        finally
        {
            if (tx is not null) await tx.DisposeAsync();
        }
    }

    /// <summary>Uma execução do motor: carrega tudo do orçamento, decide, aplica e salva.</summary>
    private sealed class Run(Saldo360DbContext db, Guid workspaceId, Guid userId, DateTimeOffset now)
    {
        private readonly DateOnly today = BusinessCalendar.Today(now);
        private readonly Dictionary<Guid, PluggyPosting> postings = [];
        private readonly Dictionary<Guid, PluggyAccountLink> links = [];
        private readonly Dictionary<Guid, PluggyImportedTransaction> imported = [];
        private readonly Dictionary<Guid, PluggyMovement> decisions = [];
        private readonly Dictionary<Guid, FinancialTransaction> coreTransactions = [];
        private readonly Dictionary<Guid, FinancialTransfer> coreTransfers = [];
        private readonly Dictionary<Guid, FinancialCreditCardInvoicePayment> corePayments = [];
        private readonly Dictionary<(Guid Card, DateOnly Closing), FinancialCreditCardInvoice> invoices = [];
        private readonly Dictionary<Guid, FinancialCreditCard> cards = [];
        private readonly Dictionary<Guid, Dictionary<string, PluggyCardBill>> bills = [];
        private IReadOnlyList<MappableCategory> categories = [];
        private Guid? fallbackCategoryId;
        private int created, updated, waiting, review, covered, transfersPaired, billsPaired, outOfRange, unchanged;

        public async Task<PluggyApplySummary> ExecuteAsync(CancellationToken ct)
        {
            foreach (var link in await db.PluggyAccountLinks.Where(l => l.WorkspaceId == workspaceId).ToListAsync(ct)) links[link.Id] = link;
            if (links.Count == 0) return new(0, 0, 0, 0, 0, 0, 0, 0, 0);

            var connectionIds = links.Values.Select(l => l.ConnectionId).Distinct().ToList();
            var accountIds = links.Values.Select(l => l.PluggyAccountId).Distinct().ToList();
            var byKey = links.Values.ToDictionary(l => (l.ConnectionId, l.PluggyAccountId));
            var rows = await db.PluggyImportedTransactions.AsNoTracking()
                .Where(t => t.WorkspaceId == workspaceId && connectionIds.Contains(t.ConnectionId) && accountIds.Contains(t.PluggyAccountId)).ToListAsync(ct);
            var linkOf = new Dictionary<Guid, PluggyAccountLink>();
            foreach (var row in rows)
                if (byKey.TryGetValue((row.ConnectionId, row.PluggyAccountId), out var link)) { imported[row.Id] = row; linkOf[row.Id] = link; }

            foreach (var posting in await db.PluggyPostings.Where(p => p.WorkspaceId == workspaceId).ToListAsync(ct)) postings[posting.ImportedTransactionId] = posting;
            categories = await db.FinancialCategories.AsNoTracking().Where(c => c.WorkspaceId == workspaceId).Select(c => new MappableCategory(c.Id, c.Name, c.Type, c.IsActive)).ToListAsync(ct);
            await LoadCoreAsync(ct);
            foreach (var group in (await db.PluggyCardBills.AsNoTracking().Where(b => b.WorkspaceId == workspaceId).ToListAsync(ct)).GroupBy(b => b.AccountLinkId))
                bills[group.Key] = group.ToDictionary(b => b.PluggyBillId, StringComparer.Ordinal);

            // 1) Decisão por transação (pura). Transações sem posting ainda ganham um, exceto as fora do período de importação.
            var order = imported.Values.OrderBy(t => t.Date).ThenBy(t => t.Id).ToList();
            foreach (var t in order)
            {
                var link = linkOf[t.Id];
                postings.TryGetValue(t.Id, out var existing);
                var movement = PluggyFinancialMapper.Decide(ImportedFacts.From(t, link), today, existing?.Resolution);
                if (movement.Disposition == PluggyDisposition.OutOfRange) { outOfRange++; continue; }
                decisions[t.Id] = movement;
                if (existing is null)
                {
                    var posting = PluggyPosting.Create(workspaceId, t.Id, link.Id, now);
                    var mapped = movement.Direction is { } direction ? PluggyCategoryMapper.Map(t.Category, direction, categories) : null;
                    if (mapped is not null) posting.SetCategory(mapped, PluggyCategorySource.Mapped, now);
                    db.PluggyPostings.Add(posting);
                    postings[t.Id] = posting;
                }
            }

            // 2) Desfaz pares cujo dado de origem mudou ou cuja outra ponta foi cancelada pelo usuário; depois pareia o que sobrou.
            UnwindBrokenPairs();
            var pairing = Pair(linkOf);

            // 3) Aplica movimentos bancários, transferências e compras de cartão; salva; só então pagamentos de fatura (dependem das faturas salvas).
            foreach (var t in order)
            {
                if (!decisions.TryGetValue(t.Id, out var movement)) continue;
                await ApplyOneAsync(t, linkOf[t.Id], movement, pairing, ct);
            }
            ApplyTransfers(pairing, linkOf);
            await db.SaveChangesAsync(ct);
            await CloseReconciledInvoicesAsync(ct);
            await db.SaveChangesAsync(ct);
            await ApplyBillPaymentsAsync(pairing, linkOf, ct);
            await db.SaveChangesAsync(ct);
            return new(created, updated, waiting, review, covered, transfersPaired, billsPaired, outOfRange, unchanged);
        }

        // ---- Carga ------------------------------------------------------------------------------------------------------

        private async Task LoadCoreAsync(CancellationToken ct)
        {
            var txIds = postings.Values.Where(p => p.FinancialTransactionId != null).Select(p => p.FinancialTransactionId!.Value).ToList();
            foreach (var t in await db.FinancialTransactions.Where(t => t.WorkspaceId == workspaceId && txIds.Contains(t.Id)).ToListAsync(ct)) coreTransactions[t.Id] = t;
            var transferIds = postings.Values.Where(p => p.FinancialTransferId != null).Select(p => p.FinancialTransferId!.Value).Distinct().ToList();
            foreach (var t in await db.FinancialTransfers.Where(t => t.WorkspaceId == workspaceId && transferIds.Contains(t.Id)).ToListAsync(ct)) coreTransfers[t.Id] = t;
            var paymentIds = postings.Values.Where(p => p.CreditCardInvoicePaymentId != null).Select(p => p.CreditCardInvoicePaymentId!.Value).ToList();
            foreach (var p in await db.FinancialCreditCardInvoicePayments.Where(p => p.WorkspaceId == workspaceId && paymentIds.Contains(p.Id)).ToListAsync(ct)) corePayments[p.Id] = p;
            var cardIds = links.Values.Where(l => l.CreditCardId != null).Select(l => l.CreditCardId!.Value).ToList();
            foreach (var c in await db.FinancialCreditCards.Where(c => c.WorkspaceId == workspaceId && cardIds.Contains(c.Id)).ToListAsync(ct)) cards[c.Id] = c;
            foreach (var i in await db.FinancialCreditCardInvoices.Where(i => i.WorkspaceId == workspaceId && cardIds.Contains(i.CreditCardId)).ToListAsync(ct)) invoices[(i.CreditCardId, i.ClosingDate)] = i;
        }

        // ---- Pares ------------------------------------------------------------------------------------------------------

        private sealed record Pairs(IReadOnlyList<(Guid Source, Guid Target)> Transfers, IReadOnlyList<(Guid Card, Guid Bank)> Bills, IReadOnlySet<Guid> TransferAmbiguous, IReadOnlySet<Guid> BillAmbiguous,
            IReadOnlySet<Guid> TransferPaired, IReadOnlySet<Guid> BillPaired);

        private static bool Settled(PluggyPosting p) => p.CounterpartPostingId is not null && p.State is PluggyPostingState.Applied or PluggyPostingState.Covered;

        private void UnwindBrokenPairs()
        {
            var byId = postings.Values.ToDictionary(p => p.Id);
            // O usuario pode ter cancelado, pelas telas existentes, o que a integracao gerou: a decisao dele prevalece e nao e recriada.
            foreach (var posting in postings.Values.Where(p => p.State == PluggyPostingState.Applied).ToList())
            {
                var cancelled = posting.Kind switch
                {
                    PluggyPostingKind.Transfer => posting.FinancialTransferId is { } tid && coreTransfers.TryGetValue(tid, out var tr) && tr.IsCancelled,
                    PluggyPostingKind.CardBillPayment => (posting.CreditCardInvoicePaymentId is { } pid && corePayments.TryGetValue(pid, out var pay) && pay.Status == FinancialCreditCardInvoicePaymentStatus.Cancelled)
                        || (posting.FinancialTransactionId is { } xid && coreTransactions.TryGetValue(xid, out var xt) && xt.IsCancelled),
                    _ => false,
                };
                if (cancelled) posting.Ignore("UserCancelled", null, now);
            }
            foreach (var posting in postings.Values.Where(Settled).ToList())
            {
                if (!decisions.TryGetValue(posting.ImportedTransactionId, out var movement)) continue;
                var counterpart = byId.GetValueOrDefault(posting.CounterpartPostingId!.Value);
                var userBroke = counterpart is null || counterpart.State is PluggyPostingState.Ignored;
                var changed = posting.State == PluggyPostingState.Applied && movement.Disposition is PluggyDisposition.TransferCandidate or PluggyDisposition.BankMovement or PluggyDisposition.BankDebitBillHint
                    && (posting.AppliedAmount != movement.Amount || posting.AppliedDate != movement.Date);
                if (!userBroke && !changed) continue;
                // Um dos lados mudou: cancela o que o par gerou e devolve as duas pontas para nova correspondência.
                if (counterpart is not null && !userBroke) UnwindPair(posting, counterpart, "SourceChanged");
                else if (posting.State == PluggyPostingState.Covered) posting.Review("BillPaymentWithoutBankDebit", now);
            }
        }

        private void UnwindPair(PluggyPosting a, PluggyPosting b, string reason)
        {
            foreach (var p in new[] { a, b })
            {
                if (p.FinancialTransferId is { } transferId) CancelTransfer(transferId);
                if (p.CreditCardInvoicePaymentId is { } paymentId) CancelInvoicePayment(paymentId);
                if (p.FinancialTransactionId is { } transactionId) CancelCore(transactionId);
                p.Review(reason, now);
            }
        }

        private void CancelTransfer(Guid transferId)
        {
            if (coreTransfers.TryGetValue(transferId, out var transfer)) transfer.Cancel(userId, now);
        }

        private void CancelInvoicePayment(Guid paymentId)
        {
            if (corePayments.TryGetValue(paymentId, out var payment)) payment.Cancel(userId, now);
        }

        private void CancelCore(Guid transactionId)
        {
            if (!coreTransactions.TryGetValue(transactionId, out var core)) return;
            core.Cancel(userId, now);
        }

        private Pairs Pair(Dictionary<Guid, PluggyAccountLink> linkOf)
        {
            PairItem Item(Guid importedId, bool hint) => new(importedId, linkOf[importedId].Id, decisions[importedId].Amount, decisions[importedId].Date, hint);
            bool Free(Guid importedId) => !Settled(postings[importedId]) && postings[importedId].State != PluggyPostingState.Ignored;

            var transferDebits = decisions.Where(d => d.Value.Disposition == PluggyDisposition.TransferCandidate && d.Value.IsDebit && Free(d.Key)).Select(d => Item(d.Key, false)).ToList();
            var transferCredits = decisions.Where(d => d.Value.Disposition == PluggyDisposition.TransferCandidate && !d.Value.IsDebit && Free(d.Key)).Select(d => Item(d.Key, false)).ToList();
            var transferResult = PluggyPairing.Match(transferDebits, transferCredits, TransferToleranceDays, differentLinks: true);

            var cardPayments = decisions.Where(d => d.Value.Disposition == PluggyDisposition.CardBillPayment && Free(d.Key)).Select(d => Item(d.Key, false)).ToList();
            // Candidatos bancários: débitos sem decisão manual. Um débito já lançado como despesa pode ser reclassificado como pagamento de fatura.
            var bankDebits = decisions.Where(d => d.Value.IsDebit && linkOf[d.Key].Kind == PluggyLinkedAccountKind.Bank && postings[d.Key].Resolution is null
                    && (d.Value.Disposition == PluggyDisposition.BankDebitBillHint
                        || (d.Value.Disposition == PluggyDisposition.BankMovement && d.Value.Reason != "UserDecision" && AppliedAsBankMovementOrNew(postings[d.Key])))
                    && !Settled(postings[d.Key]) && postings[d.Key].State != PluggyPostingState.Ignored)
                .Select(d => Item(d.Key, d.Value.Disposition == PluggyDisposition.BankDebitBillHint)).ToList();
            var billResult = PluggyPairing.Match(cardPayments, bankDebits, BillPaymentToleranceDays, differentLinks: true);

            return new(transferResult.Pairs, billResult.Pairs, transferResult.Ambiguous, billResult.Ambiguous,
                transferResult.Pairs.SelectMany(p => new[] { p.Source, p.Target }).ToHashSet(), billResult.Pairs.SelectMany(p => new[] { p.Source, p.Target }).ToHashSet());
        }

        private static bool AppliedAsBankMovementOrNew(PluggyPosting p) => p.State != PluggyPostingState.Applied || p.Kind == PluggyPostingKind.BankMovement;

        // ---- Aplicação por transação -------------------------------------------------------------------------------------

        private async Task ApplyOneAsync(PluggyImportedTransaction t, PluggyAccountLink link, PluggyMovement movement, Pairs pairs, CancellationToken ct)
        {
            var posting = postings[t.Id];
            if (posting.State == PluggyPostingState.Ignored) return;
            if (pairs.BillPaired.Contains(t.Id) || pairs.TransferPaired.Contains(t.Id)) return; // aplicado na fase de pares

            switch (movement.Disposition)
            {
                case PluggyDisposition.Waiting:
                    if (posting.State == PluggyPostingState.Applied || posting.State == PluggyPostingState.Covered) { unchanged++; break; }
                    posting.Wait(movement.Reason ?? "Pending", now); waiting++; break;
                case PluggyDisposition.Review:
                    ToReview(posting, movement.Reason ?? "Unclassified"); break;
                case PluggyDisposition.BankDebitBillHint:
                    ToReview(posting, pairs.BillAmbiguous.Contains(t.Id) ? "AmbiguousCardBillPayment" : "UnconfirmedCardBillPayment"); break;
                case PluggyDisposition.TransferCandidate:
                    ToReview(posting, pairs.TransferAmbiguous.Contains(t.Id) ? "AmbiguousTransferCounterpart" : "TransferWithoutCounterpart"); break;
                case PluggyDisposition.CardBillPayment:
                    ToReview(posting, pairs.BillAmbiguous.Contains(t.Id) ? "AmbiguousBankDebit" : "BillPaymentWithoutBankDebit"); break;
                case PluggyDisposition.BankMovement:
                    if (pairs.BillAmbiguous.Contains(t.Id)) { ToReview(posting, "AmbiguousCardBillPayment"); break; }
                    ApplyBankMovement(t, link, movement, posting); break;
                case PluggyDisposition.CardPurchase:
                    await ApplyCardPurchaseAsync(t, link, movement, posting, ct); break;
            }
        }

        /// <summary>Categoria do rastro apenas se existir no orcamento com o tipo certo; do contrario o lancamento nasce sem categoria (o usuario revisa).</summary>
        private Guid? ValidCategory(PluggyPosting posting, CategoryType type) =>
            posting.CategoryId is { } id && categories.Any(c => c.Id == id && c.Type == type) ? id : null;

        private void ToReview(PluggyPosting posting, string reason)
        {
            // O que já virou movimento permanece estável; só o que ainda não foi aplicado vai para a fila.
            if (posting.State is PluggyPostingState.Applied or PluggyPostingState.Covered) { unchanged++; return; }
            posting.Review(reason, now);
            review++;
        }

        private void ApplyBankMovement(PluggyImportedTransaction t, PluggyAccountLink link, PluggyMovement movement, PluggyPosting posting)
        {
            var type = movement.Direction!.Value;
            if (posting.State == PluggyPostingState.Applied && posting.Kind == PluggyPostingKind.BankMovement
                && posting.FinancialTransactionId is { } coreId && coreTransactions.TryGetValue(coreId, out var core))
            {
                if (core.IsCancelled) { posting.Ignore("UserCancelled", null, now); return; }
                if (core.AccountId == link.FinancialAccountId && core.Type == type)
                {
                    if (core.Amount != movement.Amount || core.TransactionDate != movement.Date || core.Description != movement.Description)
                    {
                        core.ApplyImportedSource(movement.Description, movement.Amount, movement.Date, userId, now);
                        posting.Resnapshot(movement.Amount, movement.Date, now);
                        updated++;
                    }
                    else unchanged++;
                    return;
                }
                // Conta ou sentido mudou: o lançamento antigo é cancelado e um novo é criado.
                core.Cancel(userId, now);
            }
            else if (posting.State == PluggyPostingState.Applied)
            {
                // Estava aplicado de outra forma (ex.: compra/par) e agora é movimento simples; mantém estável.
                unchanged++;
                return;
            }

            var created_ = FinancialTransaction.CreateFromPluggy(workspaceId, link.FinancialAccountId!.Value, ValidCategory(posting, type), type, movement.Description, movement.Amount, movement.Date, userId, now);
            db.FinancialTransactions.Add(created_);
            coreTransactions[created_.Id] = created_;
            posting.ApplyBankMovement(created_.Id, movement.Amount, movement.Date, type, now);
            created++;
        }

        private async Task ApplyCardPurchaseAsync(PluggyImportedTransaction t, PluggyAccountLink link, PluggyMovement movement, PluggyPosting posting, CancellationToken ct)
        {
            var card = cards[link.CreditCardId!.Value];
            if (posting.State == PluggyPostingState.Applied && posting.CreditCardPurchaseId is { } purchaseId)
            {
                var purchase = await db.FinancialCreditCardPurchases.Include(p => p.Installments).SingleAsync(p => p.WorkspaceId == workspaceId && p.Id == purchaseId, ct);
                if (purchase.Status == CreditCardPurchaseStatus.Cancelled) { posting.Ignore("UserCancelled", null, now); return; }
                if (posting.AppliedAmount == movement.Amount && posting.AppliedDate == movement.Date) { unchanged++; return; }
                // Fatura já fechada oficialmente (com o total do banco) é imutável: mudanças de origem depois disso não a alteram.
                var current = purchase.Installments.Select(i => i.InvoiceId).FirstOrDefault(id => id != null);
                if (current is { } currentId && invoices.Values.Any(i => i.Id == currentId && i.OfficialVersion > 0)) { unchanged++; return; }
                var invoice = InvoiceFor(card, link, movement.Date, t.BillId);
                if (invoice is null) { unchanged++; return; }
                purchase.UpdateSchedule(purchase.CategoryId, movement.Description, movement.Date, movement.Amount, 1, 0, null, userId, now);
                foreach (var installment in purchase.Installments.Where(i => i.Status == CreditCardInstallmentStatus.Active)) installment.AssignInvoice(invoice.Id);
                posting.Resnapshot(movement.Amount, movement.Date, now);
                updated++;
                return;
            }
            if (posting.State == PluggyPostingState.Applied) { unchanged++; return; }

            var target = InvoiceFor(card, link, movement.Date, t.BillId);
            if (target is null) { ToReview(posting, "InvoiceClosed"); return; }
            var categoryId = ValidCategory(posting, CategoryType.Expense) ?? FallbackCategory();
            var newPurchase = FinancialCreditCardPurchase.CreateImported(workspaceId, card.Id, categoryId, movement.Description, movement.Date, movement.Amount, userId, now);
            foreach (var installment in newPurchase.Installments) installment.AssignInvoice(target.Id);
            db.FinancialCreditCardPurchases.Add(newPurchase);
            posting.ApplyCardPurchase(newPurchase.Id, movement.Amount, movement.Date, now);
            created++;
        }

        /// <summary>Fatura do cartão para a data da compra (pelo calendário do cartão). Nula se a fatura já foi fechada oficialmente (importada por extrato): não se acrescenta nada a ela.</summary>
        private FinancialCreditCardInvoice? InvoiceFor(FinancialCreditCard card, PluggyAccountLink link, DateOnly date, string? billId)
        {
            // Preferência: a fatura real do banco (fechamento e vencimento informados pela Pluggy para a compra). Sem ela, o calendário do cartão.
            var cycle = billId is not null && bills.GetValueOrDefault(link.Id)?.GetValueOrDefault(billId) is { ClosingDate: { } closing, DueDate: { } due }
                ? new CreditCardInvoiceCalendar(closing.AddMonths(-1).AddDays(1), closing, due, due.ToString("yyyy-MM", System.Globalization.CultureInfo.InvariantCulture))
                : CreditCardInvoiceCalendarCalculator.ForInstallment(date, 1, card.ClosingDay, card.DueDay);
            if (!invoices.TryGetValue((card.Id, cycle.ClosingDate), out var invoice))
            {
                invoice = FinancialCreditCardInvoice.Create(workspaceId, card.Id, cycle.Competence, cycle.CycleStartDate, cycle.ClosingDate, cycle.DueDate, now);
                db.FinancialCreditCardInvoices.Add(invoice);
                invoices[(card.Id, cycle.ClosingDate)] = invoice;
            }
            return invoice.OfficialVersion > 0 ? null : invoice;
        }

        private Guid FallbackCategory()
        {
            if (fallbackCategoryId is { } known) return known;
            var existing = categories.FirstOrDefault(c => c.Type == CategoryType.Expense && PluggyFinancialMapper.Normalize(c.Name) == PluggyFinancialMapper.Normalize(FallbackCategoryName));
            if (existing is not null) return (fallbackCategoryId = existing.Id).Value;
            var category = FinancialCategory.Create(workspaceId, FallbackCategoryName, CategoryType.Expense, userId, now);
            db.FinancialCategories.Add(category);
            return (fallbackCategoryId = category.Id).Value;
        }

        // ---- Pares: transferências e pagamentos de fatura -----------------------------------------------------------------

        private void ApplyTransfers(Pairs pairs, Dictionary<Guid, PluggyAccountLink> linkOf)
        {
            foreach (var (debitId, creditId) in pairs.Transfers)
            {
                var debit = decisions[debitId]; var credit = decisions[creditId];
                var details = TransferDetails.TryCreate(linkOf[debitId].FinancialAccountId, linkOf[creditId].FinancialAccountId, debit.Amount, debit.Date, debit.Description, today, out _);
                var debitPosting = postings[debitId]; var creditPosting = postings[creditId];
                if (details is null) { ToReview(debitPosting, "TransferInvalid"); ToReview(creditPosting, "TransferInvalid"); continue; }
                var transfer = FinancialTransfer.Create(workspaceId, details, userId, now);
                db.FinancialTransfers.Add(transfer);
                // Se uma das pontas já tinha virado receita/despesa, cancela o lançamento: a transferência o substitui.
                foreach (var p in new[] { debitPosting, creditPosting })
                    if (p.FinancialTransactionId is { } id) CancelCore(id);
                debitPosting.ApplyTransfer(transfer.Id, creditPosting.Id, debit.Amount, debit.Date, now);
                creditPosting.ApplyTransfer(transfer.Id, debitPosting.Id, credit.Amount, credit.Date, now);
                transfersPaired++; created++;
            }
        }

        private async Task ApplyBillPaymentsAsync(Pairs pairs, Dictionary<Guid, PluggyAccountLink> linkOf, CancellationToken ct)
        {
            foreach (var (cardId, bankId) in pairs.Bills)
            {
                var cardPosting = postings[cardId]; var bankPosting = postings[bankId];
                var bank = decisions[bankId];
                var cardLink = linkOf[cardId]; var bankLink = linkOf[bankId];
                var invoice = await PayableInvoiceAsync(cardLink.CreditCardId!.Value, bank.Date, bank.Amount, ct);
                if (invoice is null)
                {
                    ToReview(cardPosting, "NoInvoiceForPayment"); ToReview(bankPosting, "NoInvoiceForPayment");
                    continue;
                }
                if (bankPosting.FinancialTransactionId is { } previous) CancelCore(previous); // era despesa simples: o pagamento da fatura a substitui
                var transaction = FinancialTransaction.CreateInvoicePayment(workspaceId, bankLink.FinancialAccountId!.Value, $"Pagamento de fatura {invoice.Competence}", bank.Amount, bank.Date, userId, now);
                var payment = FinancialCreditCardInvoicePayment.Create(workspaceId, invoice.Id, bankLink.FinancialAccountId.Value, transaction.Id, OperationId(bankPosting.ImportedTransactionId), bank.Date, bank.Amount, userId, now);
                db.FinancialTransactions.Add(transaction); db.FinancialCreditCardInvoicePayments.Add(payment);
                coreTransactions[transaction.Id] = transaction;
                bankPosting.ApplyBillPayment(transaction.Id, payment.Id, cardPosting.Id, bank.Amount, bank.Date, now);
                cardPosting.Cover(bankPosting.Id, now);
                billsPaired++; created++; covered++;
                await db.SaveChangesAsync(ct); // o saldo da fatura precisa refletir este pagamento antes do próximo par
            }
        }

        /// <summary>
        /// Fecha oficialmente a fatura quando o banco informou o total e a soma exata das compras importadas confere com ele. O fechamento usa a
        /// mesma auditoria das demais faturas oficiais (versão, prova de consolidação sem documento) e é o que permite registrar o pagamento. Qualquer
        /// divergência (taxas, estornos, histórico parcial, itens em revisão ou pendentes) mantém a fatura aberta e visível como divergente.
        /// </summary>
        private async Task CloseReconciledInvoicesAsync(CancellationToken ct)
        {
            foreach (var link in links.Values.Where(l => l.Kind == PluggyLinkedAccountKind.Card))
            {
                if (!bills.TryGetValue(link.Id, out var linkBills)) continue;
                foreach (var bill in linkBills.Values.Where(b => b.ClosingDate is { } closing && closing < today && b.TotalAmount is > 0m))
                {
                    if (!invoices.TryGetValue((link.CreditCardId!.Value, bill.ClosingDate!.Value), out var invoice) || invoice.OfficialVersion > 0) continue;
                    var open = imported.Values.Any(t => t.ConnectionId == link.ConnectionId && t.PluggyAccountId == link.PluggyAccountId && t.BillId == bill.PluggyBillId
                        && postings.TryGetValue(t.Id, out var posting) && posting.State is PluggyPostingState.WaitingPosted or PluggyPostingState.NeedsReview);
                    var total = await FinancialCreditCardInvoiceTotals.TotalAsync(db, workspaceId, invoice.Id, ct);
                    if (open || total <= 0m || PluggyFinancialMapper.ExactCents(bill.TotalAmount!.Value) != total) continue;
                    var snapshot = System.Text.Json.JsonSerializer.Serialize(new { Source = "Pluggy", Closing = bill.ClosingDate, Due = bill.DueDate, BankTotal = total, Installments = await db.FinancialCreditCardInstallments.CountAsync(i => i.WorkspaceId == workspaceId && i.InvoiceId == invoice.Id && i.Status == CreditCardInstallmentStatus.Active, ct) });
                    invoice.CloseOfficial(1, total, now);
                    db.FinancialCreditCardInvoiceConsolidations.Add(FinancialCreditCardInvoiceConsolidation.Create(workspaceId, link.CreditCardId.Value, invoice.Id, null,
                        Hash("pluggy-bill-document:" + link.Id.ToString("N") + ":" + bill.PluggyBillId), Hash("pluggy-bill-decision:" + total.ToString("0.00", System.Globalization.CultureInfo.InvariantCulture) + ":" + bill.ClosingDate),
                        Hash(snapshot), snapshot, 1, total, userId, now));
                }
            }
        }

        private static string Hash(string text) => Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(text)));

        /// <summary>Fatura fechada oficialmente (com o total do banco), mais antiga em aberto, onde o valor cabe. Sem isso, o pagamento vai para revisão.</summary>
        private async Task<FinancialCreditCardInvoice?> PayableInvoiceAsync(Guid cardId, DateOnly paymentDate, decimal amount, CancellationToken ct)
        {
            var candidates = invoices.Where(i => i.Key.Card == cardId && i.Value.OfficialVersion > 0 && i.Value.ClosingDate < paymentDate).Select(i => i.Value).OrderBy(i => i.DueDate).ThenBy(i => i.ClosingDate).ToList();
            foreach (var invoice in candidates)
            {
                var total = await FinancialCreditCardInvoiceTotals.TotalAsync(db, workspaceId, invoice.Id, ct);
                var paid = await FinancialCreditCardInvoiceTotals.PaidAsync(db, workspaceId, invoice.Id, ct);
                var remaining = total - paid;
                if (remaining <= 0m) continue;
                return amount <= remaining ? invoice : null;
            }
            return null;
        }

        private static Guid OperationId(Guid importedTransactionId)
        {
            var hash = SHA256.HashData(Encoding.UTF8.GetBytes("pluggy-bill-payment:" + importedTransactionId.ToString("N")));
            return new Guid(hash.AsSpan(0, 16));
        }
    }
}
