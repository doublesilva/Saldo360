using Saldo360.Domain.Pluggy;

namespace Saldo360.Application.Pluggy;

/// <summary>Como uma movimentação anterior da mesma série foi tratada: despesa decidida pelo usuário, despesa aplicada pela regra aprendida, ou qualquer outra coisa (pendente, ignorada, outra decisão).</summary>
public enum PriorTreatment { UserExpense, LearnedExpense, Other }

public sealed record CreditOperationEvidence(Guid Id, Guid AccountKey, string Operation, decimal Amount, string Description, DateOnly Date, bool IsDebit, PriorTreatment Treatment);

/// <summary>
/// Débito de operação de crédito (crediário, parcela de empréstimo) é retido para revisão porque pode ser desembolso, transferência ou pagamento de cartão. Esta regra só dispensa a revisão quando a
/// PRÓPRIA série prova o tratamento: o mesmo débito (mesma conta, operação, valor exato e descrição normalizada, dia do mês próximo) já foi decidido como despesa pelo usuário em pelo menos
/// <see cref="MinimumMonths"/> meses distintos anteriores, nenhum item igual da série ficou sem esse tratamento, e há decisão humana (não só aplicação automática) no histórico.
/// Crédito (entrada), valor novo, descrição nova ou série ambígua continuam em revisão. Nada é alterado: só devolve a decisão a aplicar.
/// </summary>
public static class PluggyCreditOperationHistory
{
    public const int MinimumMonths = 3;
    public const int MinimumUserDecisions = 2;
    public const int DayTolerance = 5;
    public const string OperationCode = "OPERACAO_CREDITO";

    public static PluggyResolution? Learn(CreditOperationEvidence candidate, IReadOnlyList<CreditOperationEvidence> all)
    {
        if (!candidate.IsDebit || candidate.Operation != OperationCode || candidate.Amount <= 0m) return null;
        var signature = PluggyFinancialMapper.Normalize(candidate.Description);
        if (signature.Length == 0) return null;
        var same = all.Where(h => h.Id != candidate.Id && h.AccountKey == candidate.AccountKey && h.IsDebit && h.Operation == OperationCode && h.Amount == candidate.Amount
                && h.Date < candidate.Date && PluggyFinancialMapper.Normalize(h.Description) == signature && Math.Abs(h.Date.Day - candidate.Date.Day) <= DayTolerance)
            .ToList();
        if (same.Count == 0 || same.Any(h => h.Treatment == PriorTreatment.Other)) return null;
        var months = same.Select(h => (h.Date.Year, h.Date.Month)).Distinct().Count();
        var userMonths = same.Where(h => h.Treatment == PriorTreatment.UserExpense).Select(h => (h.Date.Year, h.Date.Month)).Distinct().Count();
        return months >= MinimumMonths && userMonths >= MinimumUserDecisions ? PluggyResolution.AsExpense : null;
    }
}
