using Microsoft.EntityFrameworkCore;
using Saldo360.Application.CreditCards;
using Saldo360.Application.CreditCards.Projection;
using Saldo360.Application.Pluggy;
using Saldo360.Domain.CreditCards;
using Saldo360.Domain.Pluggy;
using Saldo360.Infrastructure.Persistence;

namespace Saldo360.Infrastructure.CreditCards;

/// <summary>
/// Reúne os fatos de cada cartão para o motor de obrigações (T09.4). Somente leitura e sempre no escopo do orçamento. As compras pendentes e as
/// parcelas devolvidas pela instituição são lidas das transações importadas, pela MESMA regra de decisão que as aplica quando forem lançadas
/// (<see cref="PluggyFinancialMapper.Decide"/>), para que uma parcela nunca seja interpretada de um jeito aqui e de outro no lançamento.
/// </summary>
internal sealed class CardProjectionRepository(Saldo360DbContext db, IFinancialCreditCardRepository cards, IFinancialCreditCardPurchaseRepository purchases) : ICardProjectionRepository
{
    /// <summary>Leitor para repositórios que já têm o contexto e não recebem o leitor por injeção.</summary>
    internal static CardProjectionRepository Create(Saldo360DbContext db) => new(db, new FinancialCreditCardRepository(db), new FinancialCreditCardPurchaseRepository(db));

    /// <summary>Mesmos fatos, mesmo motor: as obrigações que Agenda, saldo projetado e Dashboard consomem.</summary>
    internal static async Task<IReadOnlyList<CardObligationItem>> ReadItemsAsync(Saldo360DbContext db, Guid workspaceId, DateOnly today, CancellationToken ct) =>
        (await Create(db).LoadAsync(workspaceId, today, null, ct)).SelectMany(facts => CardProjectionEngine.Compute(facts, today).Items).ToList();

    public async Task<IReadOnlyList<CardFacts>> LoadAsync(Guid workspaceId, DateOnly today, Guid? onlyCard, CancellationToken ct)
    {
        // Cartão arquivado não aparece nas telas, mas as obrigações dele continuam valendo (fatura a pagar, parcelas conhecidas).
        var dtos = (await cards.ListAsync(workspaceId, true, ct)).Where(c => onlyCard == null || c.Id == onlyCard).OrderBy(c => c.Name, StringComparer.CurrentCultureIgnoreCase).ToList();
        if (dtos.Count == 0) return [];
        var cardIds = dtos.Select(c => c.Id).ToList();
        var accounts = await db.FinancialAccounts.AsNoTracking().Where(a => a.WorkspaceId == workspaceId)
            .Select(a => new { a.Id, a.Name, a.Scope, a.IsPrimary }).ToDictionaryAsync(a => a.Id, ct);
        var links = await db.PluggyAccountLinks.AsNoTracking()
            .Where(l => l.WorkspaceId == workspaceId && l.Kind == PluggyLinkedAccountKind.Card && l.CreditCardId != null && cardIds.Contains(l.CreditCardId.Value)).ToListAsync(ct);
        var linkByCard = links.ToDictionary(l => l.CreditCardId!.Value);
        var linkIds = links.Select(l => l.Id).ToList();

        var bills = linkIds.Count == 0 ? [] : await db.PluggyCardBills.AsNoTracking().Where(b => b.WorkspaceId == workspaceId && linkIds.Contains(b.AccountLinkId)).ToListAsync(ct);
        var billIds = bills.Select(b => b.Id).ToList();
        var billPaid = billIds.Count == 0 ? [] : (await db.PluggyCardBillPayments.AsNoTracking().Where(p => p.WorkspaceId == workspaceId && billIds.Contains(p.BillId))
            .GroupBy(p => p.BillId).Select(g => new { g.Key, Amount = g.Sum(p => p.Amount) }).ToListAsync(ct)).ToDictionary(x => x.Key, x => x.Amount);

        var invoices = await db.FinancialCreditCardInvoices.AsNoTracking().Where(i => i.WorkspaceId == workspaceId && cardIds.Contains(i.CreditCardId)).ToListAsync(ct);
        var invoiceIds = invoices.Select(i => i.Id).ToList();
        var installments = (await (from i in db.FinancialCreditCardInstallments.AsNoTracking()
                                   join p in db.FinancialCreditCardPurchases.AsNoTracking() on i.PurchaseId equals p.Id
                                   where i.WorkspaceId == workspaceId && p.WorkspaceId == workspaceId && i.InvoiceId != null && invoiceIds.Contains(i.InvoiceId.Value)
                                      && i.Status == CreditCardInstallmentStatus.Active && p.Status == CreditCardPurchaseStatus.Active
                                   group i.Amount by i.InvoiceId!.Value into g
                                   select new { Id = g.Key, Amount = g.Sum() }).ToListAsync(ct)).ToDictionary(x => x.Id, x => x.Amount);
        var historical = (await db.FinancialCreditCardImportedInstallments.AsNoTracking()
            .Where(i => i.WorkspaceId == workspaceId && i.InvoiceId != null && invoiceIds.Contains(i.InvoiceId.Value) && i.IsHistorical && i.Status == FinancialCreditCardImportedInstallmentStatus.Active)
            .GroupBy(i => i.InvoiceId!.Value).Select(g => new { Id = g.Key, Amount = g.Sum(i => i.Amount) }).ToListAsync(ct)).ToDictionary(x => x.Id, x => x.Amount);
        var adjustments = (await db.FinancialCreditCardInvoiceAdjustments.AsNoTracking()
            .Where(a => a.WorkspaceId == workspaceId && invoiceIds.Contains(a.InvoiceId) && a.Status == FinancialCreditCardInvoiceAdjustmentStatus.Active)
            .GroupBy(a => a.InvoiceId).Select(g => new { Id = g.Key, Amount = g.Sum(a => a.Nature == FinancialCreditCardInvoiceAdjustmentNature.Debit ? a.Amount : -a.Amount) }).ToListAsync(ct)).ToDictionary(x => x.Id, x => x.Amount);
        var payments = (await db.FinancialCreditCardInvoicePayments.AsNoTracking()
            .Where(p => p.WorkspaceId == workspaceId && invoiceIds.Contains(p.InvoiceId) && p.Status == FinancialCreditCardInvoicePaymentStatus.Active)
            .GroupBy(p => p.InvoiceId).Select(g => new { Id = g.Key, Amount = g.Sum(p => p.Amount) }).ToListAsync(ct)).ToDictionary(x => x.Id, x => x.Amount);

        var known = await db.FinancialCreditCardImportedInstallments.AsNoTracking()
            .Where(i => i.WorkspaceId == workspaceId && cardIds.Contains(i.CreditCardId) && i.InvoiceId == null && i.IsHistorical && i.Status == FinancialCreditCardImportedInstallmentStatus.Active)
            .Select(i => new { i.CreditCardId, i.Id, i.Amount, i.DueDate }).ToListAsync(ct);

        var connectionIds = links.Select(l => l.ConnectionId).Distinct().ToList();
        var transactions = connectionIds.Count == 0 ? [] : await db.PluggyImportedTransactions.AsNoTracking()
            .Where(t => t.WorkspaceId == workspaceId && connectionIds.Contains(t.ConnectionId)
                && (t.Status == PluggyTransactionStatus.Pending || (t.TotalInstallments != null && t.TotalInstallments > 1))).ToListAsync(ct);
        var postings = linkIds.Count == 0 ? [] : (await db.PluggyPostings.AsNoTracking().Where(p => p.WorkspaceId == workspaceId && linkIds.Contains(p.AccountLinkId))
            .Select(p => new { p.ImportedTransactionId, p.State, p.Kind, p.AppliedAmount, p.AppliedDate }).ToListAsync(ct)).ToDictionary(p => p.ImportedTransactionId);

        var result = new List<CardFacts>();
        foreach (var dto in dtos)
        {
            linkByCard.TryGetValue(dto.Id, out var link);
            var cardBills = link is null ? [] : bills.Where(b => b.AccountLinkId == link.Id).ToList();
            var limit = await purchases.GetLimitAsync(workspaceId, dto.Id, ct);
            var creditLimit = Parse(dto.CreditLimit);
            var (available, source) = link?.AvailableCreditLimit is { } bankAvailable ? (bankAvailable, "Bank") : (limit is null ? creditLimit : Parse(limit.AvailableLimit), "Saldo360");
            accounts.TryGetValue(dto.DefaultPaymentAccountId ?? Guid.Empty, out var payer);
            var info = new CardInfo(dto.Id, dto.Name, dto.Issuer, dto.LastFourDigits, dto.Scope, dto.Origin, dto.ClosingDay, dto.DueDay, creditLimit, available, source,
                payer is null ? null : dto.DefaultPaymentAccountId, payer?.Name, payer?.Scope.ToString(), payer?.IsPrimary ?? false,
                link?.Status.ToString() ?? "None", link?.LastSyncedAt, dto.IsArchived);

            var invoiceFacts = invoices.Where(i => i.CreditCardId == dto.Id).Select(i =>
            {
                var bill = cardBills.FirstOrDefault(b => b.ClosingDate == i.ClosingDate);
                var closed = i.OfficialVersion > 0 || i.IsClosedByInstitution;
                decimal? confirmed = i.InstitutionTotal ?? i.OfficialTotal;
                var composition = installments.GetValueOrDefault(i.Id) + historical.GetValueOrDefault(i.Id) + adjustments.GetValueOrDefault(i.Id);
                return new CardInvoiceFact(i.Id, i.CycleStartDate, i.ClosingDate, i.DueDate, i.Competence, closed, confirmed,
                    i.InstitutionTotal is not null ? i.InstitutionSource ?? "Institution" : i.OfficialTotal is not null ? "OfficialStatement" : null,
                    composition, payments.GetValueOrDefault(i.Id), bill?.Reconciliation.ToString() ?? "NotVerified", bill?.ReconciliationReason,
                    bill is null || !billPaid.TryGetValue(bill.Id, out var reported) ? null : reported);
            }).ToList();

            var pending = new List<PendingPurchaseFact>();
            var observations = new List<InstallmentObservation>();
            if (link is not null)
            {
                foreach (var t in transactions.Where(t => t.ConnectionId == link.ConnectionId && t.PluggyAccountId == link.PluggyAccountId))
                {
                    postings.TryGetValue(t.Id, out var posting);
                    var hasInstallments = t.TotalInstallments is > 1 && t.InstallmentNumber is >= 1 && t.InstallmentNumber <= t.TotalInstallments;
                    if (t.Status == PluggyTransactionStatus.Pending)
                    {
                        // Pendente que sumiu da origem, ou já tratado pelo usuário, não é obrigação conhecida.
                        if (t.MissingSyncCount > 0 || posting?.State is PluggyPostingState.Ignored or PluggyPostingState.Applied or PluggyPostingState.Covered) continue;
                        var decision = PluggyFinancialMapper.Decide(ImportedFacts.From(t, link) with { Status = PluggyTransactionStatus.Posted }, today);
                        if (decision.Disposition != PluggyDisposition.CardPurchase || decision.Amount <= 0m) continue;
                        var text = string.IsNullOrWhiteSpace(t.Description) ? t.DescriptionRaw ?? decision.Description : t.Description;
                        if (hasInstallments) observations.Add(new(t.Id.ToString("N"), text!, decision.Amount, t.InstallmentNumber!.Value, t.TotalInstallments!.Value, decision.Date, false, null, null));
                        else pending.Add(new(t.Id.ToString("N"), text!, decision.Amount, decision.Date));
                    }
                    else if (hasInstallments && posting is { State: PluggyPostingState.Applied, Kind: PluggyPostingKind.CardPurchase, AppliedAmount: { } applied, AppliedDate: { } date })
                    {
                        var bill = t.BillId is null ? null : cardBills.FirstOrDefault(b => b.PluggyBillId == t.BillId);
                        var text = string.IsNullOrWhiteSpace(t.Description) ? t.DescriptionRaw ?? string.Empty : t.Description;
                        observations.Add(new(t.Id.ToString("N"), text, applied, t.InstallmentNumber!.Value, t.TotalInstallments!.Value, date, true, bill?.ClosingDate, bill?.DueDate));
                    }
                }
            }

            result.Add(new(info, cardBills.Select(b => new PluggyBillFact(b.PluggyBillId, b.ClosingDate, b.DueDate, b.TotalAmount, b.Currency)).ToList(), invoiceFacts, pending, observations,
                known.Where(k => k.CreditCardId == dto.Id).Select(k => new KnownImportedFact(k.Id, k.Amount, k.DueDate)).ToList()));
        }
        return result;
    }

    private static decimal Parse(string value) => decimal.Parse(value, System.Globalization.CultureInfo.InvariantCulture);
}
