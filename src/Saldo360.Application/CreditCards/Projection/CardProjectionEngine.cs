using Saldo360.Domain.CreditCards;

namespace Saldo360.Application.CreditCards.Projection;

/// <summary>
/// Regras de obrigação do cartão (T09.4), numa só fonte para o módulo de cartões, a Agenda, o saldo projetado e o Dashboard:
/// <list type="number">
/// <item>Fatura fechada pelo banco: a obrigação é o <b>total bancário</b>. Compras, parcelas, encargos, estornos e itens pendentes do mesmo ciclo NÃO
/// são somados de novo (o banco já os considerou); a composição local só serve de conciliação.</item>
/// <item>Fatura aberta (o ciclo que fecha primeiro): composição conhecida + compras pendentes + parcelas já devolvidas pelo banco + estimadas. É provisória.</item>
/// <item>Fatura futura: parcelas conhecidas e estimadas e o que já foi lançado nela. Provisória.</item>
/// </list>
/// Pagamentos reduzem o saldo a pagar de cada fatura uma única vez; o excedente é sinalizado, nunca escondido. Nada daqui é gravado.
/// </summary>
public static class CardProjectionEngine
{
    private sealed class Cycle(DateOnly closing)
    {
        public DateOnly Closing { get; } = closing;
        public DateOnly CycleStart { get; set; }
        public DateOnly Due { get; set; }
        public string Competence { get; set; } = string.Empty;
        public CardInvoiceFact? Fact { get; set; }
        public decimal PendingKnown { get; set; }
        public decimal KnownInstallments { get; set; }
        public decimal Estimated { get; set; }
        public int Absorbed { get; set; }
    }

    public static CardProjection Compute(CardFacts facts, DateOnly today)
    {
        var card = facts.Card;
        var schedule = CardCycleSchedule.Resolve(facts.Bills, today, card.ClosingDay, card.DueDay);
        var closedByBank = facts.Invoices.Where(i => i.ClosedByBank).Select(i => i.Closing)
            .Concat(facts.Bills.Where(b => b.ClosingDate is { } c && c <= today && b.Total is not null).Select(b => b.ClosingDate!.Value)).ToList();
        DateOnly? latestClosed = closedByBank.Count == 0 ? null : closedByBank.Max();

        var cycles = new Dictionary<DateOnly, Cycle>();
        Cycle Ensure(DateOnly closing, DateOnly start, DateOnly due, string competence)
        {
            if (cycles.TryGetValue(closing, out var cycle)) return cycle;
            // A mesma competência (mês do vencimento) é a mesma fatura: um dia de fechamento ligeiramente diferente da agenda não cria uma segunda.
            var sameCompetence = cycles.Values.FirstOrDefault(c => c.Competence == competence);
            if (sameCompetence is not null) return sameCompetence;
            return cycles[closing] = new Cycle(closing) { CycleStart = start, Due = due, Competence = competence };
        }
        Cycle EnsureFrom(CreditCardInvoiceCalendar calendar) => Ensure(calendar.ClosingDate, calendar.CycleStartDate, calendar.DueDate, calendar.Competence);

        foreach (var fact in facts.Invoices) Ensure(fact.Closing, fact.CycleStart, fact.Due, fact.Competence).Fact = fact;

        // Compras pendentes (à vista): entram na fatura aberta; se o ciclo já foi fechado pelo banco, o total bancário já as absorveu.
        foreach (var pending in facts.Pending)
        {
            var calendar = schedule.CycleFor(pending.Date);
            var cycle = EnsureFrom(calendar);
            if (cycle.Fact is { ClosedByBank: true } || latestClosed is { } closed && cycle.Closing <= closed) { cycle.Absorbed++; continue; }
            cycle.PendingKnown += pending.Amount;
        }

        // Compras parceladas: parcelas já devolvidas (pendentes) e as estimadas por evidência.
        var chains = CardInstallmentProjector.Build(facts.Installments, schedule, latestClosed);
        foreach (var chain in chains)
        {
            foreach (var installment in chain.Installments.Where(i => i.Origin != InstallmentOrigin.InInvoice))
            {
                var cycle = Ensure(installment.Closing, installment.Closing.AddMonths(-1).AddDays(1), installment.Due, installment.Due.ToString("yyyy-MM"));
                if (installment.Absorbed || cycle.Fact is { ClosedByBank: true }) { cycle.Absorbed++; continue; }
                if (installment.Origin == InstallmentOrigin.Known) cycle.KnownInstallments += installment.Amount; else cycle.Estimated += installment.Amount;
            }
        }

        // O ciclo aberto sempre existe, mesmo vazio, para que o cartão tenha "a fatura atual".
        EnsureFrom(schedule.CycleFor(today));

        var nonClosed = cycles.Values.Where(c => c.Fact is not { ClosedByBank: true }).OrderBy(c => c.Closing).ToList();
        var upcoming = nonClosed.Where(c => c.Closing >= today).Select(c => (DateOnly?)c.Closing).FirstOrDefault();
        // Parcelas que a série não permite projetar não têm mês certo: sinalizam todas as faturas ainda não fechadas, sem inventar valor.
        var unprojectedTotal = chains.Sum(c => c.Unprojected);
        var ambiguousChains = chains.Count(c => c.Status == ChainStatus.Ambiguous);
        var invoices = new List<CardInvoiceProjection>();
        foreach (var cycle in cycles.Values.OrderBy(c => c.Closing))
        {
            var fact = cycle.Fact;
            var closed = fact is { ClosedByBank: true };
            var phase = closed ? InvoicePhase.Closed : upcoming is null || cycle.Closing <= upcoming ? InvoicePhase.Open : InvoicePhase.Future;
            var composition = fact?.Composition ?? 0m;
            var projected = composition + cycle.PendingKnown + cycle.KnownInstallments + cycle.Estimated;
            var confirmed = closed ? fact!.ConfirmedTotal ?? composition : (decimal?)null;
            var obligation = closed ? confirmed!.Value : projected;
            var paid = fact?.Paid ?? 0m;
            var remaining = Math.Max(0m, obligation - paid);
            var awaiting = !closed && cycle.Closing < today;
            var flags = new List<string>();
            if (awaiting) flags.Add(CardCoverageReasons.AwaitingBankBill);
            var unprojected = closed ? 0 : unprojectedTotal;
            var ambiguous = closed ? 0 : ambiguousChains;
            if (unprojected > 0) flags.Add(CardCoverageReasons.UnprojectedInstallments);
            if (ambiguous > 0) flags.Add(CardCoverageReasons.AmbiguousInstallments);
            if (!closed && !schedule.Strong && (cycle.PendingKnown + cycle.KnownInstallments + cycle.Estimated > 0)) flags.Add(CardCoverageReasons.WeakSchedule);
            var state = obligation <= 0m || paid <= 0m ? InvoicePaymentState.Unpaid : paid > obligation ? InvoicePaymentState.Overpaid : paid == obligation ? InvoicePaymentState.Paid : InvoicePaymentState.Partial;
            invoices.Add(new CardInvoiceProjection(
                card.Id, fact?.InvoiceId, cycle.Competence, cycle.CycleStart, cycle.Closing, cycle.Due, phase,
                closed ? ObligationBasis.BankTotal : phase == InvoicePhase.Open ? ObligationBasis.ProjectedOpen : ObligationBasis.ProjectedFuture,
                !closed, awaiting, confirmed, closed ? fact!.ConfirmedSource : null, composition, cycle.PendingKnown, cycle.KnownInstallments, cycle.Estimated,
                projected, obligation, paid, remaining, state, fact?.Reconciliation ?? "NotVerified", fact?.ReconciliationReason, fact?.BankReportedPaid,
                closed ? confirmed - composition : null, cycle.Absorbed, unprojected, ambiguous, flags));
        }

        var items = new List<CardObligationItem>();
        foreach (var invoice in invoices.Where(i => i.Obligation > 0m || i.Paid > 0m))
            items.Add(new(card.Id, card.PayerAccountId, invoice.Due, invoice.Obligation, invoice.Paid, invoice.Remaining, ObligationKind.Invoice, invoice.Phase, invoice.InvoiceId, invoice.Competence));
        foreach (var known in facts.KnownImported)
            items.Add(new(card.Id, card.PayerAccountId, known.DueDate, known.Amount, 0m, known.Amount, ObligationKind.KnownImported, null, null, known.DueDate?.ToString("yyyy-MM")));

        var reasons = new List<string>();
        if (invoices.Any(i => i.AwaitingBankBill)) reasons.Add(CardCoverageReasons.AwaitingBankBill);
        if (invoices.Any(i => i.UnprojectedInstallments > 0)) reasons.Add(CardCoverageReasons.UnprojectedInstallments);
        if (invoices.Any(i => i.AmbiguousChains > 0)) reasons.Add(CardCoverageReasons.AmbiguousInstallments);
        if (invoices.Any(i => i.Flags.Contains(CardCoverageReasons.WeakSchedule))) reasons.Add(CardCoverageReasons.WeakSchedule);
        if (card.PayerAccountId is null) reasons.Add(CardCoverageReasons.MissingPayerAccount);
        if (facts.KnownImported.Any(k => k.DueDate is null)) reasons.Add(CardCoverageReasons.KnownWithoutDueDate);
        if (card.SyncStatus is "ConsentExpired" or "Attention") reasons.Add(CardCoverageReasons.SyncAttention);
        if (card.Origin == "Pluggy" && latestClosed is null) reasons.Add(CardCoverageReasons.NoBankHistory);
        return new(card, schedule, invoices, chains, items, new(reasons.Count == 0, reasons));
    }
}
