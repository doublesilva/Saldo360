using Saldo360.Application.Recurrences;
using Saldo360.Domain.Categories;

namespace Saldo360.Application.Calendar;

/// <summary>Cobertura de um débito bancário pendente (Pluggy) pela agenda financeira (PROJ-03).</summary>
public enum PendingBankCoverageState
{
    /// <summary>Par único e seguro com uma obrigação já presente na agenda: adicional financeiro zero.</summary>
    Covered,
    /// <summary>Nenhuma obrigação candidata plausível na mesma conta e período: entra uma única vez na projeção de caixa.</summary>
    Uncovered,
    /// <summary>Há candidata(s), mas nenhum par seguro: não vira despesa adicional e sinaliza cobertura parcial.</summary>
    Ambiguous,
    /// <summary>Sem efeito financeiro (valor não positivo).</summary>
    Ignored,
}

public sealed record PendingBankDebit(Guid Id, Guid AccountId, decimal Amount, DateOnly Date, string Description);

/// <summary>Compromisso já representado na agenda que pode ser o mesmo débito (usado só para detectar ambiguidade, nunca para confirmar).</summary>
public sealed record PendingBankCandidate(Guid Id, Guid AccountId, decimal Amount, DateOnly Date, string Kind);

public sealed record PendingBankClassification(PendingBankDebit Debit, PendingBankCoverageState State, Guid? CoveredBy, IReadOnlyList<Guid> Candidates);

/// <summary>
/// Regra única de cobertura do <c>PendingBank</c>. A confirmação reaproveita <see cref="FixedObligationMatcher"/> (conta, tipo, descrição compatível, janela e valor esperado;
/// só pares únicos nos dois sentidos). Candidatos (mesma conta, até <see cref="CandidateDays"/> dias e valor próximo) servem apenas para detectar ambiguidade: um débito
/// com candidatas sem par seguro nunca é somado como obrigação adicional. Uma obrigação confirma no máximo um débito e deixa de ser candidata dos demais.
/// </summary>
public static class PendingBankCoverage
{
    public const int CandidateDays = 3;
    public const decimal CandidateValueTolerance = 0.10m;

    public static IReadOnlyList<PendingBankClassification> Classify(
        IReadOnlyList<PendingBankDebit> debits, IReadOnlyList<FixedObligation> obligations, IReadOnlyList<PendingBankCandidate> otherCandidates)
    {
        var expenses = obligations.Where(o => o.Type == CategoryType.Expense && o.IsPendingCashFlow).ToList();
        // Vínculo persistido (confirmação Pluggy automática ou vínculo manual): o débito já pertence a esta obrigação; não disputa par nem vira despesa adicional.
        var linked = expenses.Where(o => o.SourceTransactionId != null).GroupBy(o => o.SourceTransactionId!.Value).ToDictionary(g => g.Key, g => g.First());
        var linkedObligations = linked.Values.Select(o => o.StableId).ToHashSet();
        expenses = expenses.Where(o => !linkedObligations.Contains(o.StableId)).ToList();
        var positive = debits.Where(d => d.Amount > 0m && !linked.ContainsKey(d.Id)).ToList();
        var pairs = FixedObligationMatcher.UniquePairs(expenses, positive.Select(d => new SettlementCandidate(d.Id, d.AccountId, CategoryType.Expense, d.Description, d.Amount, d.Date)).ToList());
        var coveredBy = pairs.ToDictionary(p => p.Transaction.Id, p => p.Obligation);
        var used = pairs.Select(p => p.Obligation.StableId).ToHashSet();

        var candidates = expenses.Where(o => !used.Contains(o.StableId))
            .Select(o => new PendingBankCandidate(o.StableId, o.AccountId, o.PlannedAmount, o.DueDate, "Obligation"))
            .Concat(otherCandidates.Where(c => !used.Contains(c.Id))).ToList();

        return debits.Select(d =>
        {
            if (d.Amount <= 0m) return new PendingBankClassification(d, PendingBankCoverageState.Ignored, null, []);
            if (linked.TryGetValue(d.Id, out var link)) return new PendingBankClassification(d, PendingBankCoverageState.Covered, link.StableId, []);
            if (coveredBy.TryGetValue(d.Id, out var obligation)) return new PendingBankClassification(d, PendingBankCoverageState.Covered, obligation.StableId, []);
            var near = candidates.Where(c => IsCandidate(d, c)).Select(c => c.Id).Distinct().ToList();
            return new PendingBankClassification(d, near.Count == 0 ? PendingBankCoverageState.Uncovered : PendingBankCoverageState.Ambiguous, null, near);
        }).ToList();
    }

    private static bool IsCandidate(PendingBankDebit d, PendingBankCandidate c) =>
        c.AccountId == d.AccountId && c.Amount > 0m
        && Math.Abs(c.Date.DayNumber - d.Date.DayNumber) <= CandidateDays
        && Math.Abs(c.Amount - d.Amount) <= c.Amount * CandidateValueTolerance;
}
