namespace Saldo360.Application.Accounts.Reconciliation;

/// <summary>
/// Regras puras da reconciliação de saldo (T09.5). O saldo CALCULADO continua sendo o do núcleo financeiro (saldo inicial + movimentos do Saldo360);
/// aqui só se acrescenta uma reconstrução INDEPENDENTE do lado do banco, feita exclusivamente com as movimentações POSTED importadas, para explicar
/// a diferença sem ajustá-la:
/// <code>saldo do banco no início do dia D = saldo observado − soma das movimentações POSTED de D até a data da observação</code>
/// Só vale onde o histórico importado cobre o intervalo: antes disso é indisponível, nunca zero. Pendentes e futuras não entram; compras de cartão não são
/// movimentos da conta; o pagamento de fatura vale na data da saída bancária; transferências entram em cada conta pela sua ponta (nunca duas vezes).
/// </summary>
public static class BalanceReconciliation
{
    public const decimal Tolerance = 0.005m;

    /// <summary>
    /// Primeiro dia cujo saldo de abertura o histórico importado permite reconstruir: depois do primeiro dia com movimentação (a fronteira da coleta é
    /// incerta) e depois do último movimento que não se consegue assinar. Sem movimentação importada, não há histórico (null).
    /// </summary>
    public static DateOnly? BankSideFrom(IReadOnlyList<BankMovementFact> posted)
    {
        if (posted.Count == 0) return null;
        var boundary = posted.Min(m => m.Date);
        var unsignable = posted.Where(m => m.SignedAmount is null).Select(m => (DateOnly?)m.Date).Max();
        if (unsignable is { } last && last > boundary) boundary = last;
        return boundary.AddDays(1);
    }

    /// <summary>Saldo do banco no INÍCIO do dia (antes das movimentações dele), a partir da observação e dos movimentos POSTED até a data dela.</summary>
    public static (decimal? Amount, string? Reason) BankStartOfDay(AnchorFact anchor, IReadOnlyList<BankMovementFact> posted, DateOnly day)
    {
        if (BankSideFrom(posted) is not { } from) return (null, "NoImportedHistory");
        if (day < from) return (null, "BeforeBankHistory");
        if (day > anchor.AsOfDate.AddDays(1)) return (null, "AfterBankReference");
        var sum = posted.Where(m => m.Date >= day && m.Date <= anchor.AsOfDate).Sum(m => m.SignedAmount ?? 0m);
        return (anchor.Amount - sum, null);
    }

    public static bool SameCents(decimal a, decimal b) => Math.Abs(a - b) < Tolerance;

    /// <summary>
    /// Uma classificação por conta, na ordem: (1) sem referência bancária utilizável; (2) diferença: explicada exatamente pelas movimentações em revisão do
    /// intervalo calculado → em revisão, senão divergência identificada; (3) sem diferença: histórico calculável limitado por revisão → em revisão; limitado pelos
    /// dados do banco → histórico incompleto; senão sem divergência.
    /// </summary>
    public static (ReconciliationStatus Status, string? Reason) Classify(
        bool hasReference, string? referenceReason, decimal? difference, decimal reviewNetInRange, bool reviewLimitsHistory, string coverageQuality)
    {
        if (!hasReference) return (ReconciliationStatus.InsufficientBankReference, referenceReason ?? "NoBankObservation");
        if (difference is null) return (ReconciliationStatus.IncompleteHistory, "CalculatedBalanceUnavailable");
        if (!SameCents(difference.Value, 0m))
            return reviewNetInRange != 0m && SameCents(difference.Value, reviewNetInRange)
                ? (ReconciliationStatus.MovementsInReview, "DifferenceExplainedByReview")
                : (ReconciliationStatus.DivergenceFound, reviewNetInRange != 0m ? "PartiallyExplainedByReview" : "UnexplainedDifference");
        if (reviewLimitsHistory) return (ReconciliationStatus.MovementsInReview, "ReviewLimitsCalculableHistory");
        if (coverageQuality is "LimitedByBankData" or "None") return (ReconciliationStatus.IncompleteHistory, coverageQuality);
        return (ReconciliationStatus.NoDivergence, null);
    }
}
