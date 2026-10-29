using Saldo360.Application.Pluggy;
using Saldo360.Domain.CreditCards;

namespace Saldo360.Application.CreditCards.Projection;

/// <summary>
/// Dia de fechamento e de vencimento usados para encaixar compras e parcelas ainda não faturadas num ciclo. A fonte importa:
/// o histórico de faturas do banco (consistente) é evidência forte; só a última fatura, ou os dias cadastrados no cartão, são evidência fraca e
/// reduzem a cobertura da projeção. Esta classe NUNCA altera o cadastro do cartão (isso exige a confirmação da T09.3.2).
/// </summary>
public sealed record CardCycleSchedule(int ClosingDay, int DueDay, string Source, bool Strong)
{
    public const string BankHistory = "BankHistory";
    public const string LatestBill = "LatestBill";
    public const string Configured = "Configured";

    public static CardCycleSchedule Resolve(IReadOnlyList<PluggyBillFact> bills, DateOnly today, int configuredClosingDay, int configuredDueDay)
    {
        var suggestion = PluggyCardSchedule.Suggest(bills, today);
        if (suggestion is { ClosingDay: { } closing, DueDay: { } due }) return new(closing, due, BankHistory, true);
        var latest = bills.Where(b => b.ClosingDate is { } c && c <= today && b.DueDate is not null).OrderByDescending(b => b.ClosingDate).FirstOrDefault();
        return latest is not null ? new(latest.ClosingDate!.Value.Day, latest.DueDate!.Value.Day, LatestBill, false) : new(configuredClosingDay, configuredDueDay, Configured, false);
    }

    /// <summary>Ciclo que contém a data (a compra no dia do fechamento entra naquele ciclo).</summary>
    public CreditCardInvoiceCalendar CycleFor(DateOnly date) => CreditCardInvoiceCalendarCalculator.ForInstallment(date, 1, ClosingDay, DueDay);

    /// <summary>Ciclo que fecha <paramref name="months"/> meses depois do ciclo informado.</summary>
    public CreditCardInvoiceCalendar CycleAfter(CreditCardInvoiceCalendar cycle, int months) =>
        CreditCardInvoiceCalendarCalculator.ForInstallment(cycle.ClosingDate, 1 + months, ClosingDay, DueDay);
}
