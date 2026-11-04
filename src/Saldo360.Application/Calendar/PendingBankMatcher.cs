using Saldo360.Application.Recurrences;
using Saldo360.Domain.Categories;

namespace Saldo360.Application.Calendar;

/// <summary>Regra que sustentou a associação: a correspondência segura única (<see cref="FixedObligationMatcher"/>) ou a evidência histórica da própria recorrência.</summary>
public enum BankMatchRule { SafeMatcher, RecurrenceHistory }

public sealed record BankMatch(FixedObligation Obligation, PendingBankDebit Debit, BankMatchRule Rule);

/// <summary>Evidências de um candidato a um débito sem par seguro (diagnóstico da ambiguidade).</summary>
public sealed record BankCandidateEvidence(Guid ObligationId, Guid? RecurrenceId, string Description, decimal Amount, DateOnly DueDate, int DateDeltaDays, decimal AmountDeltaPercent,
    bool SafeMatcherDescription, bool HistoryEvidence, bool HasHistory);

/// <summary>
/// Associação automática entre débitos bancários pendentes (Pluggy) e obrigações de despesas fixas (PROJ-05). Duas regras, ambas exigindo par ÚNICO nos dois sentidos:
/// 1) a correspondência segura existente (conta, descrição compatível, janela e valor esperado); 2) a evidência histórica: a descrição bancária repete a de uma competência
/// anterior já paga da MESMA recorrência, na mesma conta, até <see cref="HistoryDays"/> dias da data esperada e com valor até <see cref="HistoryAmountTolerance"/> de diferença,
/// sem nenhuma outra obrigação plausível disputando o débito e sem outro débito com a mesma evidência. Valor igual ou descrição genérica isolados nunca associam; ambiguidade nunca vira associação.
/// </summary>
public static class PendingBankMatcher
{
    public const int HistoryDays = PendingBankCoverage.CandidateDays;
    public const decimal HistoryAmountTolerance = 0.05m;

    public static IReadOnlyList<BankMatch> Match(IReadOnlyList<PendingBankDebit> debits, IReadOnlyList<FixedObligation> obligations,
        IReadOnlyDictionary<Guid, IReadOnlyList<string>> history)
    {
        var positive = debits.Where(d => d.Amount > 0m).ToList();
        var pairs = FixedObligationMatcher.UniquePairs(obligations, positive.Select(d => new SettlementCandidate(d.Id, d.AccountId, CategoryType.Expense, d.Description, d.Amount, d.Date)).ToList());
        var debitById = positive.ToDictionary(d => d.Id);
        var result = pairs.Select(p => new BankMatch(p.Obligation, debitById[p.Transaction.Id], BankMatchRule.SafeMatcher)).ToList();

        var usedObligations = pairs.Select(p => p.Obligation.StableId).ToHashSet();
        var usedDebits = pairs.Select(p => p.Transaction.Id).ToHashSet();
        var restObligations = obligations.Where(o => !usedObligations.Contains(o.StableId)).ToList();
        var restDebits = positive.Where(d => !usedDebits.Contains(d.Id)).ToList();

        var edges = (from o in restObligations
                     where o.RecurrenceId is { } r && history.TryGetValue(r, out var h) && h.Count > 0
                     from d in restDebits
                     where HistoryCompatible(o, d, history[o.RecurrenceId!.Value])
                     select (o, d)).ToList();
        foreach (var (o, d) in edges)
        {
            var onlyEdgeForObligation = edges.Count(e => e.o.StableId == o.StableId) == 1;
            var onlyEdgeForDebit = edges.Count(e => e.d.Id == d.Id) == 1;
            var rivalObligation = restObligations.Any(x => x.StableId != o.StableId && Plausible(x, d));
            if (onlyEdgeForObligation && onlyEdgeForDebit && !rivalObligation) result.Add(new(o, d, BankMatchRule.RecurrenceHistory));
        }
        return result;
    }

    /// <summary>Por que cada candidato próximo de um débito sem par não foi associado: o que falta (histórico, descrição, valor) para automatizar com segurança.</summary>
    public static IReadOnlyList<BankCandidateEvidence> Diagnose(PendingBankDebit debit, IReadOnlyList<FixedObligation> obligations, IReadOnlyDictionary<Guid, IReadOnlyList<string>> history) =>
        obligations.Where(o => Plausible(o, debit)).Select(o =>
        {
            var descriptions = o.RecurrenceId is { } r && history.TryGetValue(r, out var h) ? h : [];
            return new BankCandidateEvidence(o.StableId, o.RecurrenceId, o.Description, o.PlannedAmount, o.DueDate, debit.Date.DayNumber - o.DueDate.DayNumber,
                o.PlannedAmount == 0m ? 0m : Math.Round((debit.Amount - o.PlannedAmount) / o.PlannedAmount * 100m, 2),
                FixedObligationMatcher.DescriptionCompatible(o.Description, debit.Description),
                descriptions.Any(x => FixedObligationMatcher.DescriptionCompatible(x, debit.Description)), descriptions.Count > 0);
        }).ToList();

    private static bool Plausible(FixedObligation o, PendingBankDebit d) =>
        o.AccountId == d.AccountId && o.PlannedAmount > 0m
        && Math.Abs(o.DueDate.DayNumber - d.Date.DayNumber) <= PendingBankCoverage.CandidateDays
        && Math.Abs(o.PlannedAmount - d.Amount) <= o.PlannedAmount * PendingBankCoverage.CandidateValueTolerance;

    private static bool HistoryCompatible(FixedObligation o, PendingBankDebit d, IReadOnlyList<string> descriptions) =>
        o.AccountId == d.AccountId && o.PlannedAmount > 0m
        && Math.Abs(o.DueDate.DayNumber - d.Date.DayNumber) <= HistoryDays
        && Math.Abs(o.PlannedAmount - d.Amount) <= o.PlannedAmount * HistoryAmountTolerance
        && descriptions.Any(x => FixedObligationMatcher.DescriptionCompatible(x, d.Description));
}
