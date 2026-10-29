using System.Security.Cryptography;
using System.Text;
using Microsoft.EntityFrameworkCore;
using Saldo360.Application.Accounts;
using Saldo360.Application.Classification;
using Saldo360.Application.Pluggy;
using Saldo360.Domain.Accounts;
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
    private const int BillEvidenceToleranceDays = 2;
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
        private readonly Dictionary<Guid, FinancialCreditCardInvoiceAdjustment> adjustments = [];
        // Identificadores externos já usados por ajustes da Pluggy (inclui os cancelados): o índice único os considera, então um crédito
        // recriado depois de cancelado precisa de um identificador novo.
        private readonly HashSet<(Guid Card, string ExternalId)> usedAdjustmentExternalIds = [];
        private readonly Dictionary<Guid, List<PluggyCardBillPayment>> billPayments = [];
        private readonly Dictionary<Guid, PluggyCardBill> billById = [];
        private readonly Dictionary<Guid, PluggyCardBillPayment> billPaymentById = [];
        private int created, updated, waiting, review, covered, transfersPaired, billsPaired, outOfRange, unchanged;
        private int rehomed, creditsApplied, closuresRecorded, staleInvoicesRemoved, billsEvaluated, anchorsUpdated;

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
            var billRows = await db.PluggyCardBills.Where(b => b.WorkspaceId == workspaceId).ToListAsync(ct);
            foreach (var group in billRows.GroupBy(b => b.AccountLinkId)) bills[group.Key] = group.ToDictionary(b => b.PluggyBillId, StringComparer.Ordinal);
            foreach (var bill in billRows) billById[bill.Id] = bill;
            foreach (var payment in await db.PluggyCardBillPayments.AsNoTracking().Where(p => p.WorkspaceId == workspaceId).ToListAsync(ct))
            {
                billPaymentById[payment.Id] = payment;
                if (!billPayments.TryGetValue(payment.BillId, out var list)) billPayments[payment.BillId] = list = [];
                list.Add(payment);
            }

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
                    // Categoria na criação: só etapas locais (regra do domínio e categoria da Pluggy já existente). Nunca IA nem criação de categoria na sincronização.
                    if (movement.Direction is { } direction) ClassifyNewPosting(posting, t, link, movement, direction);
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
            // 4) Fechamento informado pela instituição (independe de conciliação), pagamentos de fatura reconhecidos, limpeza de faturas calculadas
            // que ficaram vazias, estado de conciliação de cada fatura e referência de saldo das contas. Tudo idempotente.
            RecordInstitutionClosures();
            await db.SaveChangesAsync(ct);
            await ApplyBillPaymentsAsync(pairing, linkOf, ct);
            await db.SaveChangesAsync(ct);
            await RemoveStaleInvoicesAsync(ct);
            await db.SaveChangesAsync(ct);
            await EvaluateBillsAsync(ct);
            await UpdateBalanceAnchorsAsync(ct);
            await db.SaveChangesAsync(ct);
            return new PluggyApplySummary(created, updated, waiting, review, covered, transfersPaired, billsPaired, outOfRange, unchanged)
            {
                PurchasesRehomed = rehomed, CardCreditsApplied = creditsApplied, InvoicesClosedByInstitution = closuresRecorded, StaleInvoicesRemoved = staleInvoicesRemoved,
                BillsEvaluated = billsEvaluated, BalanceAnchorsUpdated = anchorsUpdated,
            };
        }

        private void ClassifyNewPosting(PluggyPosting posting, PluggyImportedTransaction t, PluggyAccountLink link, PluggyMovement movement, CategoryType direction)
        {
            var input = new FinancialClassificationInput(posting.Id.ToString("N"), movement.Description, movement.Amount, movement.Date,
                direction == CategoryType.Income ? FinancialMovementKind.Income : FinancialMovementKind.Expense, t.Category, t.OperationType, "Pluggy",
                IsCardPurchase: link.Kind == PluggyLinkedAccountKind.Card);
            if (FinancialClassificationRules.Evaluate(input) is { } rule)
            {
                posting.RecordClassification(null, PluggyCategorySource.Rule, FinancialClassificationVersions.Rules, rule.Reason, rule.NeedsReview, now);
                return;
            }
            if (FinancialExternalCategoryCatalog.ResolveExisting(t.Category, direction, categories) is { } mapped)
                posting.RecordClassification(mapped, PluggyCategorySource.Pluggy, FinancialClassificationVersions.Catalog, "Categoria informada pelo banco", false, now);
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
            var adjustmentIds = postings.Values.Where(p => p.CreditCardInvoiceAdjustmentId != null).Select(p => p.CreditCardInvoiceAdjustmentId!.Value).ToList();
            foreach (var a in await db.FinancialCreditCardInvoiceAdjustments.Where(a => a.WorkspaceId == workspaceId && adjustmentIds.Contains(a.Id)).ToListAsync(ct)) adjustments[a.Id] = a;
            foreach (var used in await db.FinancialCreditCardInvoiceAdjustments.AsNoTracking().Where(a => a.WorkspaceId == workspaceId && a.ExternalId.StartsWith("pluggy:"))
                         .Select(a => new { a.CreditCardId, a.ExternalId }).ToListAsync(ct)) usedAdjustmentExternalIds.Add((used.CreditCardId, used.ExternalId));
            var cardIds = links.Values.Where(l => l.CreditCardId != null).Select(l => l.CreditCardId!.Value).ToList();
            foreach (var c in await db.FinancialCreditCards.Where(c => c.WorkspaceId == workspaceId && cardIds.Contains(c.Id)).ToListAsync(ct)) cards[c.Id] = c;
            foreach (var i in await db.FinancialCreditCardInvoices.Where(i => i.WorkspaceId == workspaceId && cardIds.Contains(i.CreditCardId)).ToListAsync(ct)) invoices[(i.CreditCardId, i.ClosingDate)] = i;
        }

        // ---- Pares ------------------------------------------------------------------------------------------------------

        private sealed record Pairs(IReadOnlyList<(Guid Source, Guid Target)> Transfers, IReadOnlyList<(Guid Card, Guid Bank)> Bills, IReadOnlySet<Guid> TransferAmbiguous, IReadOnlySet<Guid> BillAmbiguous,
            IReadOnlySet<Guid> TransferPaired, IReadOnlySet<Guid> BillPaired, IReadOnlyList<(Guid BillPaymentId, Guid Bank)> BillEvidence);

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
            // Pagamento reconhecido por payments[] da fatura (sem ponta no cartão): se a saída bancária mudou, o pagamento é desfeito e reavaliado.
            foreach (var posting in postings.Values.Where(p => p.State == PluggyPostingState.Applied && p.Kind == PluggyPostingKind.CardBillPayment && p.CounterpartPostingId is null).ToList())
            {
                if (!decisions.TryGetValue(posting.ImportedTransactionId, out var movement)) continue;
                if (movement.Disposition is not (PluggyDisposition.BankMovement or PluggyDisposition.TransferProbe or PluggyDisposition.BankDebitBillHint)) continue;
                if (posting.AppliedAmount == movement.Amount && posting.AppliedDate == movement.Date) continue;
                if (posting.CreditCardInvoicePaymentId is { } paymentId) CancelInvoicePayment(paymentId);
                if (posting.FinancialTransactionId is { } transactionId) CancelCore(transactionId);
                posting.Review("SourceChanged", now);
            }
            foreach (var posting in postings.Values.Where(Settled).ToList())
            {
                if (!decisions.TryGetValue(posting.ImportedTransactionId, out var movement)) continue;
                var counterpart = byId.GetValueOrDefault(posting.CounterpartPostingId!.Value);
                var userBroke = counterpart is null || counterpart.State is PluggyPostingState.Ignored;
                var changed = posting.State == PluggyPostingState.Applied && movement.Disposition is PluggyDisposition.TransferCandidate or PluggyDisposition.TransferProbe or PluggyDisposition.BankMovement or PluggyDisposition.BankDebitBillHint
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

            // Transferências só se casam com a mesma operação do banco nas duas pontas (PIX com PIX, TED com TED...); nunca por descrição.
            var transferPairs = new List<(Guid Source, Guid Target)>();
            var transferAmbiguous = new HashSet<Guid>();
            var transferSides = decisions.Where(d => d.Value.Disposition is PluggyDisposition.TransferCandidate or PluggyDisposition.TransferProbe && Free(d.Key))
                .GroupBy(d => imported[d.Key].OperationType?.ToUpperInvariant() ?? string.Empty);
            foreach (var operation in transferSides)
            {
                var debits = operation.Where(d => d.Value.IsDebit).Select(d => Item(d.Key, false)).ToList();
                var credits = operation.Where(d => !d.Value.IsDebit).Select(d => Item(d.Key, false)).ToList();
                var result = PluggyPairing.Match(debits, credits, TransferToleranceDays, differentLinks: true);
                transferPairs.AddRange(result.Pairs);
                transferAmbiguous.UnionWith(result.Ambiguous);
            }
            var transferResult = new PairingResult(transferPairs, transferAmbiguous);

            var cardPayments = decisions.Where(d => d.Value.Disposition == PluggyDisposition.CardBillPayment && Free(d.Key)).Select(d => Item(d.Key, false)).ToList();
            // Candidatos bancários: débitos sem decisão manual. Um débito já lançado como despesa pode ser reclassificado como pagamento de fatura.
            // PIX/TED que já formou transferência com outra conta integrada não pode ser, ao mesmo tempo, pagamento de fatura.
            var transferPaired = transferResult.Pairs.SelectMany(p => new[] { p.Source, p.Target }).ToHashSet();
            var bankDebits = decisions.Where(d => d.Value.IsDebit && linkOf[d.Key].Kind == PluggyLinkedAccountKind.Bank && postings[d.Key].Resolution is null && !transferPaired.Contains(d.Key)
                    && !(postings[d.Key].State == PluggyPostingState.Applied && postings[d.Key].Kind == PluggyPostingKind.CardBillPayment)
                    && (d.Value.Disposition == PluggyDisposition.BankDebitBillHint
                        || (d.Value.Disposition is PluggyDisposition.BankMovement or PluggyDisposition.TransferProbe && d.Value.Reason != "UserDecision" && AppliedAsBankMovementOrNew(postings[d.Key])))
                    && !Settled(postings[d.Key]) && postings[d.Key].State != PluggyPostingState.Ignored)
                .Select(d => Item(d.Key, d.Value.Disposition == PluggyDisposition.BankDebitBillHint)).ToList();
            var billResult = PluggyPairing.Match(cardPayments, bankDebits, BillPaymentToleranceDays, differentLinks: true);

            // Segunda evidência estruturada: payments[] informados pelo banco dentro da própria fatura (valor exato, data próxima, correspondência única).
            // Só concorre o débito que o banco rotulou como pagamento de cartão (categoria estruturada) ou que a descrição sugere; nunca a descrição sozinha.
            var cardPaired = billResult.Pairs.SelectMany(p => new[] { p.Source, p.Target }).ToHashSet();
            var consumed = billResult.Pairs.Select(p => (Amount: decisions[p.Target].Amount, Date: decisions[p.Target].Date)).ToList();
            // Pagamentos já registrados em rodadas anteriores (débito liquidado como pagamento de fatura) também explicam o payments[] correspondente:
            // sem isto, num reprocessamento o mesmo pagamento do banco voltaria a concorrer com outro débito de mesmo valor e data próxima.
            consumed.AddRange(postings.Values
                .Where(p => p.State == PluggyPostingState.Applied && p.Kind == PluggyPostingKind.CardBillPayment && p.CreditCardInvoicePaymentId != null && decisions.ContainsKey(p.ImportedTransactionId) && decisions[p.ImportedTransactionId].IsDebit)
                .Select(p => (Amount: decisions[p.ImportedTransactionId].Amount, Date: decisions[p.ImportedTransactionId].Date)));
            var evidenceItems = new List<PairItem>();
            foreach (var link in links.Values.Where(l => l.Kind == PluggyLinkedAccountKind.Card && l.CreditCardId != null))
            {
                if (!bills.TryGetValue(link.Id, out var linkBills)) continue;
                foreach (var bill in linkBills.Values.Where(b => b.ClosingDate is { } closing && closing <= today && b.TotalAmount is not null))
                    foreach (var payment in billPayments.GetValueOrDefault(bill.Id) ?? [])
                    {
                        // Este mesmo pagamento já foi reconhecido pelo crédito no cartão: não concorre com outro débito.
                        var index = consumed.FindIndex(c => c.Amount == payment.Amount && Math.Abs(c.Date.DayNumber - payment.PaymentDate.DayNumber) <= BillEvidenceToleranceDays);
                        if (index >= 0) { consumed.RemoveAt(index); continue; }
                        evidenceItems.Add(new PairItem(payment.Id, link.Id, payment.Amount, payment.PaymentDate, false));
                    }
            }
            var evidenceDebits = bankDebits.Where(d => !cardPaired.Contains(d.Id) && IsLabeledAsCardPayment(imported[d.Id], decisions[d.Id])).ToList();
            var evidenceResult = PluggyPairing.Match(evidenceItems, evidenceDebits, BillEvidenceToleranceDays, differentLinks: true);
            var billAmbiguous = billResult.Ambiguous.Concat(evidenceResult.Ambiguous.Where(id => evidenceDebits.Any(d => d.Id == id))).ToHashSet();

            return new(transferResult.Pairs, billResult.Pairs, transferResult.Ambiguous, billAmbiguous,
                transferResult.Pairs.SelectMany(p => new[] { p.Source, p.Target }).ToHashSet(),
                cardPaired.Concat(evidenceResult.Pairs.Select(p => p.Target)).ToHashSet(),
                evidenceResult.Pairs.Select(p => (BillPaymentId: p.Source, Bank: p.Target)).ToList());
        }

        private static readonly HashSet<string> PaymentReasons = new(StringComparer.Ordinal)
            { "BillPaymentWithoutBankDebit", "AmbiguousBankDebit", "NoInvoiceForPayment", "AmbiguousInvoiceForPayment", "PaymentExceedsInvoice" };

        private static bool IsLabeledAsCardPayment(PluggyImportedTransaction t, PluggyMovement movement) =>
            movement.Disposition == PluggyDisposition.BankDebitBillHint || FinancialCategoryMatching.Key(t.Category) == "credit card payment";

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
                case PluggyDisposition.TransferProbe:
                    // Sem ponta oposta numa conta integrada é receita/despesa comum (PIX/TED com terceiros); só a dúvida real vai para revisão.
                    if (pairs.TransferAmbiguous.Contains(t.Id)) ToReview(posting, "AmbiguousTransferCounterpart");
                    else if (pairs.BillAmbiguous.Contains(t.Id)) ToReview(posting, "AmbiguousCardBillPayment");
                    else ApplyBankMovement(t, link, movement, posting);
                    break;
                case PluggyDisposition.CardBillPayment:
                    ToReview(posting, pairs.BillAmbiguous.Contains(t.Id) ? "AmbiguousBankDebit" : "BillPaymentWithoutBankDebit"); break;
                case PluggyDisposition.BankMovement:
                    if (pairs.BillAmbiguous.Contains(t.Id)) { ToReview(posting, "AmbiguousCardBillPayment"); break; }
                    ApplyBankMovement(t, link, movement, posting); break;
                case PluggyDisposition.CardPurchase:
                    await ApplyCardPurchaseAsync(t, link, movement, posting, ct); break;
                case PluggyDisposition.CardCredit:
                    ApplyCardCredit(t, link, movement, posting); break;
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
                // A fatura certa é a do banco (fechamento real, por billId). Compras alocadas antes por calendário migram para ela, sem mudar valor nem data.
                if (t.BillId is not null && bills.GetValueOrDefault(link.Id)?.ContainsKey(t.BillId) == true
                    && purchase.Installments.Select(i => i.InvoiceId).FirstOrDefault(id => id != null) is { } homeId && !invoices.Values.Any(i => i.Id == homeId && i.OfficialVersion > 0)
                    && InvoiceFor(card, link, movement.Date, t.BillId) is { } home && home.Id != homeId)
                {
                    foreach (var installment in purchase.Installments.Where(i => i.Status == CreditCardInstallmentStatus.Active)) installment.AssignInvoice(home.Id);
                    rehomed++;
                    return;
                }
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
            // Evidência 1: crédito de pagamento no cartão casado com o débito bancário (valor exato, data próxima, correspondência única).
            foreach (var (cardId, bankId) in pairs.Bills)
            {
                var cardPosting = postings[cardId]; var bankPosting = postings[bankId];
                var bank = decisions[bankId];
                var (invoice, problem) = await ResolvePayableInvoiceAsync(linkOf[cardId].CreditCardId!.Value, bank.Date, bank.Amount, ct);
                if (invoice is null)
                {
                    var reason = problem ?? "NoInvoiceForPayment";
                    ToReview(cardPosting, reason); ToReview(bankPosting, reason);
                    continue;
                }
                await RecordInvoicePaymentAsync(bankPosting, linkOf[bankId], invoice, bank, cardPosting, ct);
            }

            // Evidência 2: payments[] da própria fatura do banco. A fatura é conhecida (a do pagamento), então só é preciso caber no saldo dela.
            foreach (var (paymentId, bankId) in pairs.BillEvidence)
            {
                var evidence = billPaymentById[paymentId]; var bill = billById[evidence.BillId];
                var cardLink = links[bill.AccountLinkId]; var bankPosting = postings[bankId]; var bank = decisions[bankId];
                if (bill.ClosingDate is not { } closing || !invoices.TryGetValue((cardLink.CreditCardId!.Value, closing), out var invoice) || invoice.InstitutionTotal is not { } bankTotal)
                { ToReview(bankPosting, "NoInvoiceForPayment"); continue; }
                var remaining = bankTotal - await FinancialCreditCardInvoiceTotals.PaidAsync(db, workspaceId, invoice.Id, ct);
                if (bank.Amount > remaining) { ToReview(bankPosting, "PaymentExceedsInvoice"); continue; }
                await RecordInvoicePaymentAsync(bankPosting, linkOf[bankId], invoice, bank, null, ct);
            }
        }

        /// <summary>
        /// Registra a liquidação: a saída bancária deixa de ser despesa simples e passa a ser pagamento da fatura (saída de caixa, sem consumo),
        /// preservando o dinheiro que saiu. Parcial é permitido; não depende da conciliação das compras.
        /// </summary>
        private async Task RecordInvoicePaymentAsync(PluggyPosting bankPosting, PluggyAccountLink bankLink, FinancialCreditCardInvoice invoice, PluggyMovement bank, PluggyPosting? cardPosting, CancellationToken ct)
        {
            if (bankPosting.FinancialTransactionId is { } previous) CancelCore(previous); // era despesa simples: o pagamento da fatura a substitui
            var transaction = FinancialTransaction.CreateInvoicePayment(workspaceId, bankLink.FinancialAccountId!.Value, $"Pagamento de fatura {invoice.Competence}", bank.Amount, bank.Date, userId, now);
            var payment = FinancialCreditCardInvoicePayment.Create(workspaceId, invoice.Id, bankLink.FinancialAccountId.Value, transaction.Id, OperationId(bankPosting.ImportedTransactionId), bank.Date, bank.Amount, userId, now);
            db.FinancialTransactions.Add(transaction); db.FinancialCreditCardInvoicePayments.Add(payment);
            coreTransactions[transaction.Id] = transaction;
            bankPosting.ApplyBillPayment(transaction.Id, payment.Id, cardPosting?.Id, bank.Amount, bank.Date, now);
            if (cardPosting is not null) { cardPosting.Cover(bankPosting.Id, now); covered++; }
            billsPaired++; created++;
            await db.SaveChangesAsync(ct); // o saldo pago da fatura precisa refletir este pagamento antes do próximo
        }

        /// <summary>
        /// Fatura que o pagamento quita, só quando inequívoca. Considera faturas fechadas pela instituição (total bancário preservado) já vencidas
        /// para pagamento (fechamento até a data do pagamento) com saldo a pagar suficiente: exatamente uma que feche o valor, ou uma única candidata
        /// (pagamento parcial). Mais de uma opção = ambíguo (revisão); nenhuma = sem fatura.
        /// </summary>
        private async Task<(FinancialCreditCardInvoice? Invoice, string? Problem)> ResolvePayableInvoiceAsync(Guid cardId, DateOnly paymentDate, decimal amount, CancellationToken ct)
        {
            var candidates = new List<(FinancialCreditCardInvoice Invoice, decimal Remaining, bool Evidence)>();
            foreach (var invoice in invoices.Where(i => i.Key.Card == cardId && i.Value.InstitutionTotal is not null && i.Value.ClosingDate <= paymentDate).Select(i => i.Value))
            {
                var remaining = invoice.InstitutionTotal!.Value - await FinancialCreditCardInvoiceTotals.PaidAsync(db, workspaceId, invoice.Id, ct);
                if (remaining < amount || remaining <= 0m) continue;
                var reported = ReportedPayments(cardId, invoice);
                var evidence = reported.Any(p => p.Amount == amount && Math.Abs(p.PaymentDate.DayNumber - paymentDate.DayNumber) <= BillEvidenceToleranceDays);
                // O banco já informa a fatura como paga por outros pagamentos e nenhum deles é este: ela não é candidata a receber mais um.
                if (!evidence && reported.Count > 0 && reported.Sum(p => p.Amount) >= invoice.InstitutionTotal.Value) continue;
                candidates.Add((invoice, remaining, evidence));
            }
            // Referência estruturada primeiro: o pagamento aparece em payments[] de exatamente uma fatura.
            var proven = candidates.Where(c => c.Evidence).ToList();
            if (proven.Count == 1) return (proven[0].Invoice, null);
            var exact = candidates.Where(c => c.Remaining == amount).ToList();
            if (exact.Count == 1) return (exact[0].Invoice, null);
            if (exact.Count > 1) return (null, "AmbiguousInvoiceForPayment");
            return candidates.Count switch { 1 => (candidates[0].Invoice, null), 0 => (null, null), _ => (null, "AmbiguousInvoiceForPayment") };
        }

        /// <summary>Identificador estável do ajuste: "pluggy:{id}" na primeira vez; se já existir (cancelado por mudança na origem), "pluggy:{id}:v2", ":v3"...</summary>
        private string NextAdjustmentExternalId(Guid cardId, Guid transactionId)
        {
            var baseId = "pluggy:" + transactionId.ToString("N");
            var candidate = baseId;
            for (var version = 2; usedAdjustmentExternalIds.Contains((cardId, candidate)); version++) candidate = baseId + ":v" + version;
            return candidate;
        }

        private List<PluggyCardBillPayment> ReportedPayments(Guid cardId, FinancialCreditCardInvoice invoice)
        {
            var link = links.Values.FirstOrDefault(l => l.CreditCardId == cardId);
            var bill = link is not null && bills.TryGetValue(link.Id, out var linkBills) ? linkBills.Values.FirstOrDefault(b => b.ClosingDate == invoice.ClosingDate) : null;
            return bill is not null && billPayments.TryGetValue(bill.Id, out var list) ? list : [];
        }

        /// <summary>
        /// Estorno, devolução ou cashback no cartão: crédito da fatura em que aparece (reduz a obrigação e, com a categoria da compra, o consumo da
        /// categoria). Só com referência estruturada à fatura do banco; nunca é receita e não se duplica em reprocessamento (identificador externo estável).
        /// </summary>
        private void ApplyCardCredit(PluggyImportedTransaction t, PluggyAccountLink link, PluggyMovement movement, PluggyPosting posting)
        {
            var card = cards[link.CreditCardId!.Value];
            if (posting.State == PluggyPostingState.Applied && posting.CreditCardInvoiceAdjustmentId is { } adjustmentId)
            {
                if (!adjustments.TryGetValue(adjustmentId, out var current)) { unchanged++; return; }
                if (current.Status == FinancialCreditCardInvoiceAdjustmentStatus.Cancelled) { posting.Ignore("UserCancelled", null, now); return; }
                if (posting.AppliedAmount == movement.Amount && posting.AppliedDate == movement.Date) { unchanged++; return; }
                if (invoices.Values.Any(i => i.Id == current.InvoiceId && i.OfficialVersion > 0)) { unchanged++; return; }
                // A origem mudou: o ajuste não é editável, então este é cancelado e recriado com o valor atual.
                current.Cancel(userId, now);
                posting.Review("SourceChanged", now);
            }
            else if (posting.State == PluggyPostingState.Applied) { unchanged++; return; }

            if (t.BillId is null || bills.GetValueOrDefault(link.Id)?.GetValueOrDefault(t.BillId) is not { ClosingDate: not null }) { ToReview(posting, "CreditWithoutBillReference"); return; }
            var invoice = InvoiceFor(card, link, movement.Date, t.BillId);
            if (invoice is null) { ToReview(posting, "InvoiceClosed"); return; }
            var refund = movement.Reason != "CardCashback";
            var categoryId = refund ? FinancialExternalCategoryCatalog.ResolveExisting(t.Category, CategoryType.Expense, categories) : null;
            var description = movement.Description.Length > FinancialCreditCardInvoiceAdjustment.DescriptionMaxLength ? movement.Description[..FinancialCreditCardInvoiceAdjustment.DescriptionMaxLength] : movement.Description;
            var externalId = NextAdjustmentExternalId(card.Id, t.Id);
            var adjustment = FinancialCreditCardInvoiceAdjustment.Create(workspaceId, card.Id, invoice.Id,
                refund ? FinancialCreditCardInvoiceAdjustmentType.Refund : FinancialCreditCardInvoiceAdjustmentType.Credit, FinancialCreditCardInvoiceAdjustmentNature.Credit,
                movement.Date, description, movement.Amount, externalId, categoryId, userId, now);
            usedAdjustmentExternalIds.Add((card.Id, externalId));
            db.FinancialCreditCardInvoiceAdjustments.Add(adjustment);
            adjustments[adjustment.Id] = adjustment;
            posting.ApplyCardCredit(adjustment.Id, movement.Amount, movement.Date, now);
            created++; creditsApplied++;
        }

        // ---- Faturas: fechamento, conciliação e limpeza ---------------------------------------------------------------------

        /// <summary>
        /// Registra o fechamento informado pela instituição: a data de fechamento da fatura do banco chegou e há total. É independente de conciliação e
        /// não cria ajustes: o total bancário é guardado como veio. Fatura só com vencimento (sem fechamento) nunca é considerada fechada.
        /// </summary>
        private void RecordInstitutionClosures()
        {
            foreach (var link in links.Values.Where(l => l.Kind == PluggyLinkedAccountKind.Card && l.CreditCardId != null))
            {
                if (!bills.TryGetValue(link.Id, out var linkBills)) continue;
                var cardId = link.CreditCardId!.Value;
                foreach (var bill in linkBills.Values.Where(b => b.ClosingDate is { } c && c <= today && b.DueDate is not null && b.TotalAmount is not null
                    && string.Equals(b.Currency ?? "BRL", "BRL", StringComparison.OrdinalIgnoreCase)))
                {
                    // O total bancário exato fica em pluggy_card_bills (4 casas). Na fatura vai o valor em centavos; saldo credor (negativo) não é obrigação.
                    var total = Math.Max(0m, decimal.Round(bill.TotalAmount!.Value, 2, MidpointRounding.AwayFromZero));
                    var closing = bill.ClosingDate!.Value;
                    if (!invoices.TryGetValue((cardId, closing), out var invoice))
                    {
                        if (total == 0m && (billPayments.GetValueOrDefault(bill.Id)?.Count ?? 0) == 0) continue;
                        var due = bill.DueDate!.Value;
                        invoice = FinancialCreditCardInvoice.Create(workspaceId, cardId, due.ToString("yyyy-MM", System.Globalization.CultureInfo.InvariantCulture), closing.AddMonths(-1).AddDays(1), closing, due, now);
                        db.FinancialCreditCardInvoices.Add(invoice);
                        invoices[(cardId, closing)] = invoice;
                    }
                    var previous = invoice.InstitutionTotal;
                    invoice.RecordInstitutionClosure(total, "Pluggy", now);
                    if (previous != total) closuresRecorded++;
                }
            }
        }

        /// <summary>
        /// Remove faturas calculadas por calendário que ficaram sem nenhum componente depois de as compras irem para a fatura real do banco. Só
        /// remove o que não tem parcela, ajuste, pagamento, consolidação nem conciliação, não é oficial e não é fechada pela instituição.
        /// </summary>
        private async Task RemoveStaleInvoicesAsync(CancellationToken ct)
        {
            foreach (var link in links.Values.Where(l => l.Kind == PluggyLinkedAccountKind.Card && l.CreditCardId != null))
            {
                var cardId = link.CreditCardId!.Value;
                var bankClosings = bills.GetValueOrDefault(link.Id)?.Values.Where(b => b.ClosingDate is not null).Select(b => b.ClosingDate!.Value).ToHashSet();
                if (bankClosings is null || bankClosings.Count == 0) continue;
                foreach (var invoice in invoices.Where(i => i.Key.Card == cardId).Select(i => i.Value).ToList())
                {
                    if (bankClosings.Contains(invoice.ClosingDate) || invoice.OfficialVersion > 0 || invoice.IsClosedByInstitution) continue;
                    var id = invoice.Id;
                    var referenced = await db.FinancialCreditCardInstallments.AnyAsync(i => i.InvoiceId == id, ct)
                        || await db.FinancialCreditCardImportedInstallments.AnyAsync(i => i.InvoiceId == id, ct)
                        || await db.FinancialCreditCardInvoiceAdjustments.AnyAsync(a => a.InvoiceId == id, ct)
                        || await db.FinancialCreditCardInvoicePayments.AnyAsync(p => p.InvoiceId == id, ct)
                        || await db.FinancialCreditCardInvoiceConsolidations.AnyAsync(c => c.InvoiceId == id, ct)
                        || await db.FinancialPlannedInvoiceReconciliations.AnyAsync(r => r.CreditCardInvoiceId == id, ct);
                    if (referenced) continue;
                    db.FinancialCreditCardInvoices.Remove(invoice);
                    invoices.Remove((cardId, invoice.ClosingDate));
                    staleInvoicesRemoved++;
                }
            }
        }

        /// <summary>
        /// Conciliação por fatura, independente do fechamento e do pagamento: o total bancário (preservado) × o que o Saldo360 importou (compras,
        /// encargos e créditos). Pendentes/em revisão da fatura ou histórico que não a cobre inteira = incompleta; sem isso, igual = conciliada,
        /// diferente = divergente. A diferença só é registrada: nunca é compensada com ajuste.
        /// </summary>
        private async Task EvaluateBillsAsync(CancellationToken ct)
        {
            foreach (var link in links.Values.Where(l => l.Kind == PluggyLinkedAccountKind.Card && l.CreditCardId != null))
            {
                if (!bills.TryGetValue(link.Id, out var linkBills)) continue;
                var cardId = link.CreditCardId!.Value;
                var linkTransactions = imported.Values.Where(t => t.ConnectionId == link.ConnectionId && t.PluggyAccountId == link.PluggyAccountId).ToList();
                var earliest = linkTransactions.Where(t => t.Date is not null).Select(t => PluggyFinancialMapper.BusinessDate(t.Date!.Value)).DefaultIfEmpty().Min();
                foreach (var bill in linkBills.Values)
                {
                    billsEvaluated++;
                    if (bill.ClosingDate is not { } closing || closing > today || bill.TotalAmount is not { } bankTotal)
                    { bill.SetReconciliation(PluggyBillReconciliation.NotVerified, "Open", null, now); continue; }
                    var invoice = invoices.GetValueOrDefault((cardId, closing));
                    var components = invoice is null ? 0m : await FinancialCreditCardInvoiceTotals.TotalAsync(db, workspaceId, invoice.Id, ct);
                    // Evidência de pagamento sem saída bancária (ou ambígua) é assunto do pagamento, não da composição da fatura: não a torna incompleta.
                    var openItems = linkTransactions.Any(t => t.BillId == bill.PluggyBillId && postings.TryGetValue(t.Id, out var p)
                        && (p.State == PluggyPostingState.WaitingPosted || (p.State == PluggyPostingState.NeedsReview && !PaymentReasons.Contains(p.Reason ?? string.Empty))));
                    var cycleStart = closing.AddMonths(-1).AddDays(1);
                    var (state, reason) =
                        openItems ? (PluggyBillReconciliation.Incomplete, "OpenItems")
                        : earliest != default && earliest > cycleStart || link.ImportFrom is { } from && from > cycleStart ? (PluggyBillReconciliation.Incomplete, "MissingHistory")
                        : invoice is null && bankTotal > 0m ? (PluggyBillReconciliation.Incomplete, "NoImportedItems")
                        : decimal.Round(bankTotal, 2, MidpointRounding.AwayFromZero) == components ? (PluggyBillReconciliation.Reconciled, null)
                        : (PluggyBillReconciliation.Divergent, "TotalMismatch");
                    bill.SetReconciliation(state, reason, components, now);
                }
            }
        }

        // ---- Referência de saldo (âncora) ----------------------------------------------------------------------------------

        /// <summary>
        /// Guarda o saldo observado de cada conta bancária integrada como referência (âncora) e deriva o saldo no início do intervalo coberto por
        /// movimentos consistentes: observado menos os movimentos do intervalo. O intervalo começa depois do último movimento postado que ficou fora
        /// (em revisão ou ignorado); pendentes não entram. Não cria lançamento nem ajuste e não toca o saldo inicial manual, que prevalece.
        /// </summary>
        private async Task UpdateBalanceAnchorsAsync(CancellationToken ct)
        {
            foreach (var link in links.Values.Where(l => l.Kind == PluggyLinkedAccountKind.Bank && l.FinancialAccountId != null))
            {
                if (link.ObservedBalance is not { } observed || link.ObservedAt is not { } observedRaw) continue;
                // O PostgreSQL guarda microssegundos; sem normalizar, o instante em memória (100 ns) pareceria sempre diferente do gravado.
                var observedAt = new DateTimeOffset(observedRaw.UtcTicks / 10 * 10, TimeSpan.Zero);
                var accountId = link.FinancialAccountId!.Value;
                var asOf = BusinessCalendar.Today(observedAt);
                var kind = link.PluggySubtype is "CHECKING_ACCOUNT" or "SAVINGS_ACCOUNT" || string.Equals(link.PluggyType, "BANK", StringComparison.OrdinalIgnoreCase)
                    ? BalanceAnchorKind.CurrentBalance : BalanceAnchorKind.Unknown;
                var cents = PluggyFinancialMapper.ExactCents(observed);

                var current = await db.FinancialAccountBalanceAnchors.SingleOrDefaultAsync(a => a.AccountId == accountId && a.IsCurrent, ct);
                if (current is null || current.ObservedAt != observedAt || current.Amount != (cents ?? observed))
                {
                    current?.Supersede();
                    await db.SaveChangesAsync(ct); // libera o índice de "vigente" antes de criar a nova referência
                    current = FinancialAccountBalanceAnchor.Create(workspaceId, accountId, cents ?? observed, observedAt, asOf, kind, "Pluggy", link.Id, now);
                    db.FinancialAccountBalanceAnchors.Add(current);
                }

                string? unusable =
                    cents is null ? "BalancePrecision"
                    : link.Status != PluggyAccountLinkStatus.Active ? "ConnectionNotActive"
                    : link.LastSyncedAt is null ? "NeverSyncedCompletely"
                    : kind == BalanceAnchorKind.Unknown ? "BalanceNatureUnknown"
                    // Saldo positivo com cheque especial em uso é incoerente: a natureza do saldo (contábil × disponível) não está clara.
                    : observed > 0m && link.OverdraftUsedLimit is > 0m ? "BalanceNatureAmbiguous"
                    : null;
                var applied = postings.Values.Where(p => p.AccountLinkId == link.Id && p.State == PluggyPostingState.Applied && p.AppliedDate != null).Select(p => p.AppliedDate!.Value).ToList();
                var start = link.ImportFrom ?? (applied.Count > 0 ? applied.Min() : (DateOnly?)null);
                if (unusable is null && start is null) unusable = "NoImportedHistory";
                if (unusable is not null) { current.Derive(BalanceAnchorReliability.Unreliable, unusable, null, null); anchorsUpdated++; continue; }

                // Movimento postado que o Saldo360 não contabiliza (revisão ou ignorado) limita o histórico calculável até a sua data.
                var excluded = imported.Values.Where(t => t.ConnectionId == link.ConnectionId && t.PluggyAccountId == link.PluggyAccountId && t.Status == PluggyTransactionStatus.Posted && t.Date != null
                    && postings.TryGetValue(t.Id, out var p) && (p.State == PluggyPostingState.NeedsReview || (p.State == PluggyPostingState.Ignored && p.Reason != "BeforeImportFrom")))
                    .Select(t => PluggyFinancialMapper.BusinessDate(t.Date!.Value)).ToList();
                var reference = excluded.Count == 0 ? start!.Value : excluded.Max().AddDays(1) > start!.Value ? excluded.Max().AddDays(1) : start.Value;

                var from = reference; var through = asOf;
                var rows = await db.FinancialTransactions.AsNoTracking().Where(t => t.WorkspaceId == workspaceId && t.AccountId == accountId && !t.IsCancelled && t.TransactionDate >= from && t.TransactionDate <= through)
                    .GroupBy(t => t.Type).Select(g => new { g.Key, Sum = g.Sum(x => x.Amount) }).ToListAsync(ct);
                var transferIn = await db.FinancialTransfers.AsNoTracking().Where(t => t.WorkspaceId == workspaceId && !t.IsCancelled && t.DestinationAccountId == accountId && t.TransferDate >= from && t.TransferDate <= through).SumAsync(t => (decimal?)t.Amount, ct) ?? 0m;
                var transferOut = await db.FinancialTransfers.AsNoTracking().Where(t => t.WorkspaceId == workspaceId && !t.IsCancelled && t.SourceAccountId == accountId && t.TransferDate >= from && t.TransferDate <= through).SumAsync(t => (decimal?)t.Amount, ct) ?? 0m;
                var income = rows.Where(r => r.Key == CategoryType.Income).Sum(r => r.Sum); var expense = rows.Where(r => r.Key == CategoryType.Expense).Sum(r => r.Sum);
                var opening = FinancialBalanceCalculator.ApplyMovements(cents!.Value, -income, -expense, -transferIn, -transferOut);
                current.Derive(reference > start.Value ? BalanceAnchorReliability.Provisional : BalanceAnchorReliability.Reliable,
                    reference > start.Value ? "ExcludedMovementsLimitHistory" : null, opening, reference);
                anchorsUpdated++;
            }
        }

        private static Guid OperationId(Guid importedTransactionId)
        {
            var hash = SHA256.HashData(Encoding.UTF8.GetBytes("pluggy-bill-payment:" + importedTransactionId.ToString("N")));
            return new Guid(hash.AsSpan(0, 16));
        }
    }
}
