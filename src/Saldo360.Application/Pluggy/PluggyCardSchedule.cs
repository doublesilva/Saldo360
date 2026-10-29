namespace Saldo360.Application.Pluggy;

/// <summary>Dia habitual de fechamento e de vencimento deduzido do histórico de faturas do banco. Cada um só é sugerido quando o histórico é consistente.</summary>
public sealed record PluggyCardScheduleSuggestion(int? ClosingDay, int? DueDay, int SampleSize, string? ClosingReason, string? DueReason);

/// <summary>
/// Fechamento e vencimento são coisas distintas: o fechamento vem de <c>billClosingDate</c> e o vencimento de <c>dueDate</c>, nunca um no lugar do
/// outro. A sugestão usa as últimas faturas já fechadas e só aponta um dia quando a maioria delas concorda; caso contrário não inventa data.
/// </summary>
public static class PluggyCardSchedule
{
    public const int Sample = 6;
    public const int MinimumSample = 3;
    /// <summary>Fração mínima das faturas recentes que precisam concordar no mesmo dia (2/3).</summary>
    private const int MinimumAgreementPercent = 66;

    public static PluggyCardScheduleSuggestion Suggest(IReadOnlyList<PluggyBillFact> bills, DateOnly today)
    {
        var recent = bills.Where(b => b.ClosingDate is { } closing && closing <= today && b.DueDate is not null)
            .OrderByDescending(b => b.ClosingDate).Take(Sample).ToList();
        var (closingDay, closingReason) = Consistent(recent.Select(b => b.ClosingDate!.Value.Day).ToList());
        var (dueDay, dueReason) = Consistent(recent.Select(b => b.DueDate!.Value.Day).ToList());
        return new(closingDay, dueDay, recent.Count, closingReason, dueReason);
    }

    private static (int? Day, string? Reason) Consistent(IReadOnlyList<int> days)
    {
        if (days.Count < MinimumSample) return (null, "NotEnoughHistory");
        var top = days.GroupBy(d => d).OrderByDescending(g => g.Count()).ThenByDescending(g => g.Key).First();
        return top.Count() * 100 >= days.Count * MinimumAgreementPercent ? (top.Key, null) : (null, "Inconsistent");
    }
}
