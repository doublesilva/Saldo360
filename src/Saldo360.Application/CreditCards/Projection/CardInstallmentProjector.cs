using System.Text.RegularExpressions;
using Saldo360.Application.Pluggy;
using Saldo360.Domain.CreditCards;

namespace Saldo360.Application.CreditCards.Projection;

/// <summary>
/// Reconstrói as compras parceladas a partir das parcelas que a instituição devolveu (número e total) e estima as que faltam, somente com evidência.
/// Regras: (1) a identidade da compra é cartão + descrição sem o sufixo "NN/MM" + total de parcelas; compras iguais em paralelo ou com valores
/// incompatíveis ficam <see cref="ChainStatus.Ambiguous"/> e nunca são estimadas; (2) parcela devolvida jamais é recriada nem duplicada: uma parcela já faturada
/// vale mais que a pendente do mesmo número; (3) só se estima para a frente (ou num buraco futuro entre duas parcelas conhecidas), nunca no passado;
/// (4) estimar exige série viva (a última parcela conhecida está no ciclo aberto ou na última fatura fechada), valor conhecido e periodicidade mensal
/// provada por duas parcelas ou pelo ciclo forte do cartão; (5) nada é gravado: o resultado é leitura.
/// </summary>
public static class CardInstallmentProjector
{
    private static readonly Regex Suffix = new(@"[\s\-\*]*\(?\s*\d{1,2}\s*/\s*\d{1,2}\s*\)?\s*$", RegexOptions.Compiled | RegexOptions.CultureInvariant);

    public static IReadOnlyList<InstallmentChain> Build(
        IReadOnlyList<InstallmentObservation> observations, CardCycleSchedule schedule, DateOnly? latestClosedClosing)
    {
        var chains = new List<InstallmentChain>();
        foreach (var group in observations.Where(o => o.Total > 1 && o.Number >= 1 && o.Number <= o.Total).GroupBy(o => (Key: BaseKey(o.Description), o.Total)))
            chains.Add(BuildChain(group.Key.Key, group.Key.Total, group.ToList(), schedule, latestClosedClosing));
        return chains.OrderBy(c => c.Description, StringComparer.Ordinal).ThenBy(c => c.Total).ToList();
    }

    /// <summary>Descrição sem o sufixo de parcela, sem acentos, em minúsculas: a mesma compra em faturas diferentes tem a mesma chave.</summary>
    public static string BaseKey(string? description)
    {
        var text = PluggyFinancialMapper.Normalize(description);
        string previous;
        do { previous = text; text = Suffix.Replace(text, string.Empty).Trim(); } while (text != previous && text.Length > 0);
        return text.Length == 0 ? "sem descricao" : text;
    }

    private sealed record Kept(InstallmentObservation Obs, CreditCardInvoiceCalendar Cycle);

    private static InstallmentChain BuildChain(string key, int total, List<InstallmentObservation> observations, CardCycleSchedule schedule, DateOnly? latestClosed)
    {
        CreditCardInvoiceCalendar CycleOf(InstallmentObservation o) =>
            o.BillClosingDate is { } closing
                ? new(closing.AddMonths(-1).AddDays(1), closing, o.BillDueDate ?? schedule.CycleFor(closing).DueDate, (o.BillDueDate ?? schedule.CycleFor(closing).DueDate).ToString("yyyy-MM"))
                : schedule.CycleFor(o.Date);

        // Por número: a parcela já faturada vence a pendente; duas iguais no mesmo número são compras paralelas (ambíguo).
        var kept = new List<Kept>();
        var parallel = false;
        foreach (var byNumber in observations.GroupBy(o => o.Number).OrderBy(g => g.Key))
        {
            var billed = byNumber.Where(o => o.InInvoice).ToList();
            var chosen = billed.Count > 0 ? billed : byNumber.ToList();
            if (chosen.Count > 1) parallel = true;
            kept.AddRange(chosen.Select(o => new Kept(o, CycleOf(o))));
        }

        var description = observations.OrderByDescending(o => o.Number).First().Description;
        var distinct = kept.GroupBy(k => k.Obs.Number).Select(g => g.First()).OrderBy(k => k.Obs.Number).ToList();
        var last = distinct[^1];
        var tolerance = total * 0.01m;
        var inconsistentAmounts = kept.Any(k => Math.Abs(k.Obs.Amount - last.Obs.Amount) > tolerance);
        var monthly = distinct.Count >= 2
            ? distinct.Zip(distinct.Skip(1)).All(p => MonthIndex(p.Second.Cycle.ClosingDate) - MonthIndex(p.First.Cycle.ClosingDate) == p.Second.Obs.Number - p.First.Obs.Number)
            : (bool?)null;
        var alive = latestClosed is null || last.Cycle.ClosingDate >= latestClosed;

        var items = kept.Select(k => new ProjectedInstallment(
            k.Obs.Number, total, k.Obs.Amount, k.Cycle.ClosingDate, k.Cycle.DueDate,
            k.Obs.InInvoice ? InstallmentOrigin.InInvoice : InstallmentOrigin.Known,
            Absorbed: !k.Obs.InInvoice && latestClosed is { } closed && k.Cycle.ClosingDate <= closed, k.Obs.SourceId)).ToList();

        string? ambiguity = parallel ? "ParallelIdenticalPurchases" : inconsistentAmounts ? "InconsistentAmounts" : null;
        string? blocker = null;
        var status = ChainStatus.Complete;

        var missing = Enumerable.Range(1, total).Except(distinct.Select(k => k.Obs.Number)).Where(n => n > distinct[0].Obs.Number).OrderBy(n => n).ToList();
        var unprojected = 0;
        var pastGap = false;
        foreach (var number in missing)
        {
            var anchor = distinct.Last(k => k.Obs.Number < number);
            var projected = schedule.CycleAfter(anchor.Cycle, number - anchor.Obs.Number);
            var isTail = number > last.Obs.Number;
            if (latestClosed is { } closedBill && projected.ClosingDate <= closedBill) { pastGap = true; continue; } // buraco no passado: só o banco preenche
            var evidence = monthly == true || (distinct.Count == 1 && schedule.Strong);
            if (ambiguity is not null || !evidence || (isTail ? !alive : monthly != true))
            {
                unprojected++;
                blocker ??= ambiguity is not null ? null : !alive && isTail ? "Interrupted" : "InsufficientEvidence";
                continue;
            }
            items.Add(new ProjectedInstallment(number, total, last.Obs.Amount, projected.ClosingDate, projected.DueDate, InstallmentOrigin.Estimated, false, null));
        }

        string? reason = null;
        if (ambiguity is not null && unprojected > 0) { status = ChainStatus.Ambiguous; reason = ambiguity; }
        else if (blocker == "Interrupted") { status = ChainStatus.Interrupted; reason = blocker; }
        else if (blocker == "InsufficientEvidence") { status = ChainStatus.InsufficientEvidence; reason = blocker; }
        else if (pastGap) { status = ChainStatus.GapDetected; reason = "MissingPastInstallment"; }
        else if (items.Any(i => i.Origin == InstallmentOrigin.Estimated)) status = ChainStatus.Estimated;

        return new(Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(System.Text.Encoding.UTF8.GetBytes($"{key}|{total}")))[..12].ToLowerInvariant(),
            description, total, last.Obs.Amount, status, reason, items.OrderBy(i => i.Number).ThenBy(i => i.Closing).ToList(), unprojected);
    }

    private static int MonthIndex(DateOnly date) => date.Year * 12 + date.Month;
}
