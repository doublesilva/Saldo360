using Microsoft.EntityFrameworkCore;
using Saldo360.Application.CreditCards;
using Saldo360.Application.CreditCards.Projection;
using Saldo360.Application.Pluggy;
using Saldo360.Domain.CreditCards;
using Saldo360.Domain.Pluggy;
using Saldo360.Domain.Recurrences;
using Saldo360.Application.Recurrences;
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
    /// <summary>Cobrança recorrente detectada é projetada até o maior horizonte da projeção consolidada (36 meses) mais um mês de folga; a leitura recorta pelo período pedido. Compras futuras nunca são inventadas.</summary>
    public const int RecurringHorizonMonths = 37;

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
                                      && i.Status == CreditCardInstallmentStatus.Active && p.Status == CreditCardPurchaseStatus.Active && p.OperationKind == CreditCardOperationKind.Normal
                                   group i.Amount by i.InvoiceId!.Value into g
                                   select new { Id = g.Key, Amount = g.Sum() }).ToListAsync(ct)).ToDictionary(x => x.Id, x => x.Amount);
        // Financiamento de saldo de fatura já lançado: continua na obrigação da fatura, mas não é composição de consumo.
        var financedByInvoice = (await (from i in db.FinancialCreditCardInstallments.AsNoTracking()
                                        join p in db.FinancialCreditCardPurchases.AsNoTracking() on i.PurchaseId equals p.Id
                                        where i.WorkspaceId == workspaceId && p.WorkspaceId == workspaceId && i.InvoiceId != null && invoiceIds.Contains(i.InvoiceId.Value)
                                           && i.Status == CreditCardInstallmentStatus.Active && p.Status == CreditCardPurchaseStatus.Active && p.OperationKind == CreditCardOperationKind.InvoiceFinancing
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
        var postedSinglePurchases = connectionIds.Count == 0 ? [] : await db.PluggyImportedTransactions.AsNoTracking()
            .Where(t => t.WorkspaceId == workspaceId && connectionIds.Contains(t.ConnectionId)
                && t.Status == PluggyTransactionStatus.Posted && (t.TotalInstallments == null || t.TotalInstallments <= 1)).ToListAsync(ct);
        var postings = linkIds.Count == 0 ? [] : (await db.PluggyPostings.AsNoTracking().Where(p => p.WorkspaceId == workspaceId && linkIds.Contains(p.AccountLinkId))
            .Select(p => new { p.ImportedTransactionId, p.State, p.Kind, p.AppliedAmount, p.AppliedDate }).ToListAsync(ct)).ToDictionary(p => p.ImportedTransactionId);
        var detectedRecurrences = await db.FinancialRecurrences.AsNoTracking().Where(r => r.WorkspaceId == workspaceId
            && r.Origin == RecurrenceOrigin.PluggyDetected && r.Status == RecurrenceStatus.Active && r.FinancialCreditCardId != null
            && cardIds.Contains(r.FinancialCreditCardId.Value)).ToListAsync(ct);
        var manualRecurrences = await db.FinancialRecurrences.AsNoTracking().Where(r => r.WorkspaceId == workspaceId
            && r.Origin == RecurrenceOrigin.Manual && r.Status == RecurrenceStatus.Active && r.FinancialCreditCardId != null
            && cardIds.Contains(r.FinancialCreditCardId.Value)).ToListAsync(ct);
        var manualIds = manualRecurrences.Select(r => r.Id).ToList();
        // Vínculo persistido: ocorrência materializada já é compra do cartão (compõe a fatura), então a projeção não a repete.
        var materialized = manualIds.Count == 0 ? [] : (await db.FinancialRecurrenceMaterializations.AsNoTracking()
            .Where(m => m.WorkspaceId == workspaceId && manualIds.Contains(m.RecurrenceId)).Select(m => new { m.RecurrenceId, m.OccurrenceNumber }).ToListAsync(ct))
            .Select(m => (m.RecurrenceId, m.OccurrenceNumber)).ToHashSet();

        // Categoria da série: a de suas parcelas já lançadas (compras ativas). Só vale quando é única e não é o marcador "Outros"; parcelas projetadas herdam, nada é gravado.
        var fallbackCategories = (await db.FinancialCategories.AsNoTracking().Where(c => c.WorkspaceId == workspaceId).Select(c => new { c.Id, c.Name }).ToListAsync(ct))
            .Where(c => PluggyFinancialMapper.Normalize(c.Name) == "outros").Select(c => c.Id).ToHashSet();
        var seriesRows = await (from p in db.PluggyPostings.AsNoTracking()
                                join i in db.PluggyImportedTransactions.AsNoTracking() on p.ImportedTransactionId equals i.Id
                                join pu in db.FinancialCreditCardPurchases.AsNoTracking() on p.CreditCardPurchaseId equals pu.Id
                                where p.WorkspaceId == workspaceId && p.State == PluggyPostingState.Applied && p.Kind == PluggyPostingKind.CardPurchase && i.TotalInstallments > 1
                                    && pu.Status == CreditCardPurchaseStatus.Active && cardIds.Contains(pu.CreditCardId)
                                select new { pu.CreditCardId, pu.CategoryId, i.Description, i.DescriptionRaw, Total = i.TotalInstallments }).ToListAsync(ct);
        var seriesByCard = seriesRows.Where(r => r.Total is > 1 && !fallbackCategories.Contains(r.CategoryId))
            .GroupBy(r => (r.CreditCardId, Key: $"{CardInstallmentProjector.BaseKey(string.IsNullOrWhiteSpace(r.Description) ? r.DescriptionRaw : r.Description)}|{r.Total}"))
            .Where(g => g.Select(r => r.CategoryId).Distinct().Count() == 1)
            .ToLookup(g => g.Key.CreditCardId, g => (g.Key.Key, Category: g.First().CategoryId));

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

            // FAT-01: faturas fechadas antes da data inicial de importação são histórico (fora do corte): o banco as devolve em /bills, mas a composição e os pagamentos anteriores ao corte
            // não são importados, então elas nunca podem ser obrigação a pagar nem vencidas. Só vale para cartão vinculado com data de início.
            var invoiceFacts = invoices.Where(i => i.CreditCardId == dto.Id && (link?.ImportFrom is not { } importFrom || i.ClosingDate >= importFrom)).Select(i =>
            {
                var bill = cardBills.FirstOrDefault(b => b.ClosingDate == i.ClosingDate);
                var closed = i.OfficialVersion > 0 || i.IsClosedByInstitution;
                decimal? confirmed = i.InstitutionTotal ?? i.OfficialTotal;
                var composition = installments.GetValueOrDefault(i.Id) + historical.GetValueOrDefault(i.Id) + adjustments.GetValueOrDefault(i.Id);
                return new CardInvoiceFact(i.Id, i.CycleStartDate, i.ClosingDate, i.DueDate, i.Competence, closed, confirmed,
                    i.InstitutionTotal is not null ? i.InstitutionSource ?? "Institution" : i.OfficialTotal is not null ? "OfficialStatement" : null,
                    composition, payments.GetValueOrDefault(i.Id), bill?.Reconciliation.ToString() ?? "NotVerified", bill?.ReconciliationReason,
                    bill is null || !billPaid.TryGetValue(bill.Id, out var reported) ? null : reported, financedByInvoice.GetValueOrDefault(i.Id));
            }).ToList();

            var pending = new List<PendingPurchaseFact>();
            var observations = new List<InstallmentObservation>();
            // Financiamento de saldo de fatura: só com a evidência da própria série (parcelas estruturadas + categoria "Credit card payment" + cronograma idêntico), nunca pela descrição sozinha.
            static string DescriptionOf(PluggyImportedTransaction t) => t.Description ?? t.DescriptionRaw ?? string.Empty;
            var financingSeries = link is null ? new HashSet<string>() : InvoiceFinancingEvidence.ConfirmedSeries(transactions
                .Where(t => t.ConnectionId == link.ConnectionId && t.PluggyAccountId == link.PluggyAccountId)
                .Select(t => new FinancingObservation(dto.Id, DescriptionOf(t), Math.Abs(t.Amount ?? 0m), t.TotalInstallments, t.Category)));
            bool IsFinancing(PluggyImportedTransaction t) => InvoiceFinancingEvidence.IsFinancing(new(dto.Id, DescriptionOf(t), Math.Abs(t.Amount ?? 0m), t.TotalInstallments, t.Category), financingSeries);
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
                        if (hasInstallments) observations.Add(new(t.Id.ToString("N"), text!, decision.Amount, t.InstallmentNumber!.Value, t.TotalInstallments!.Value, decision.Date, false, null, null, IsFinancing(t)));
                        else pending.Add(new(t.Id.ToString("N"), text!, decision.Amount, decision.Date));
                    }
                    else if (hasInstallments && posting is { State: PluggyPostingState.Applied, Kind: PluggyPostingKind.CardPurchase, AppliedAmount: { } applied, AppliedDate: { } date })
                    {
                        var bill = t.BillId is null ? null : cardBills.FirstOrDefault(b => b.PluggyBillId == t.BillId);
                        var text = string.IsNullOrWhiteSpace(t.Description) ? t.DescriptionRaw ?? string.Empty : t.Description;
                        observations.Add(new(t.Id.ToString("N"), text, applied, t.InstallmentNumber!.Value, t.TotalInstallments!.Value, date, true, bill?.ClosingDate, bill?.DueDate, IsFinancing(t)));
                    }
                }
            }

            var recurring = new List<RecurringChargeFact>();
            var cardSchedule = CardCycleSchedule.Resolve(cardBills.Select(b => new PluggyBillFact(b.PluggyBillId, b.ClosingDate, b.DueDate, b.TotalAmount, b.Currency)).ToList(), today, dto.ClosingDay, dto.DueDay);
            var cardManual = manualRecurrences.Where(r => r.FinancialCreditCardId == dto.Id).ToList();
            // A fatura aberta começa antes de hoje, e a anterior ainda não venceu quando o ciclo fechou mas o banco não devolveu o total: ambas seguem compostas pelas assinaturas do período
            // (o motor absorve o que o banco já fechou).
            var openCycle = cardSchedule.CycleFor(today);
            var previousCycle = cardSchedule.CycleFor(openCycle.CycleStartDate.AddDays(-1));
            var manualFrom = previousCycle.DueDate >= today ? previousCycle.CycleStartDate : openCycle.CycleStartDate;
            // Cobrança efetiva já importada do banco no mesmo ciclo (mesma descrição) substitui a projeção.
            bool AlreadyObserved(string description, DateOnly chargeDate) => link is not null && postedSinglePurchases.Any(t => t.ConnectionId == link.ConnectionId && t.PluggyAccountId == link.PluggyAccountId
                && t.Status == PluggyTransactionStatus.Posted && t.Date is { } date
                && PluggyFinancialMapper.Normalize(t.Description ?? t.DescriptionRaw) == PluggyFinancialMapper.Normalize(description)
                && CardCycleSchedule.Resolve([], today, dto.ClosingDay, dto.DueDay).CycleFor(PluggyFinancialMapper.BusinessDate(date)).Competence == cardSchedule.CycleFor(chargeDate).Competence);
            foreach (var recurrence in cardManual)
            {
                IReadOnlyList<RecurrenceOccurrenceDto> occurrences;
                try { occurrences = FinancialRecurrenceCalendar.CalculateOccurrences(recurrence, manualFrom, today.AddMonths(RecurringHorizonMonths)); } catch (ArgumentException) { continue; }
                foreach (var occurrence in occurrences.Where(o => !materialized.Contains((recurrence.Id, o.Sequence)) && !AlreadyObserved(recurrence.Description, o.Date)))
                    recurring.Add(new(recurrence.Id, recurrence.Description, occurrence.Amount, occurrence.Date, recurrence.IsEstimated, true, occurrence.Sequence, recurrence.FinancialCategoryId));
            }
            // A definição do usuário prevalece sobre a detecção só quando é comprovadamente a mesma assinatura (mesmo cartão, mensal, mesma descrição normalizada E mesmo valor).
            // Descrição igual com valor diferente pode ser outra assinatura: preserva as duas (o Razão sinaliza a possível duplicidade para revisão).
            foreach (var recurrence in detectedRecurrences.Where(r => r.FinancialCreditCardId == dto.Id && !cardManual.Any(m => SameSubscription(m, r))))
            {
                var occurrences = FinancialRecurrenceCalendar.CalculateOccurrences(recurrence, today, today.AddMonths(RecurringHorizonMonths));
                foreach (var occurrence in occurrences.Where(o => !AlreadyObserved(recurrence.Description, o.Date)))
                    recurring.Add(new(recurrence.Id, recurrence.Description, recurrence.Amount, occurrence.Date, recurrence.IsEstimated));
            }
            result.Add(new(info, cardBills.Select(b => new PluggyBillFact(b.PluggyBillId, b.ClosingDate, b.DueDate, b.TotalAmount, b.Currency)).ToList(), invoiceFacts, pending, observations,
                known.Where(k => k.CreditCardId == dto.Id).Select(k => new KnownImportedFact(k.Id, k.Amount, k.DueDate)).ToList(), recurring,
                seriesByCard[dto.Id].ToDictionary(x => x.Key, x => x.Category)));
        }
        return result;
    }

    /// <summary>Evidência suficiente de que duas definições são a mesma assinatura: mesmo cartão, mensal, descrição normalizada e valor.</summary>
    internal static bool SameSubscription(FinancialRecurrence manual, FinancialRecurrence detected) =>
        manual.FinancialCreditCardId == detected.FinancialCreditCardId && manual.Frequency == RecurrenceFrequency.Monthly && detected.Frequency == RecurrenceFrequency.Monthly
        && PluggyFinancialMapper.Normalize(manual.Description) == PluggyFinancialMapper.Normalize(detected.Description) && manual.Amount == detected.Amount;

    private static decimal Parse(string value) => decimal.Parse(value, System.Globalization.CultureInfo.InvariantCulture);
}
