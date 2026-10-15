using Saldo360.Domain.Common;

namespace Saldo360.Domain.CreditCards;

public enum FinancialCreditCardInvoiceStatus { Open, Closed }

public sealed class FinancialCreditCardInvoice : Entity
{
    private FinancialCreditCardInvoice() { }
    public Guid FamilyId { get; private init; }
    public Guid CreditCardId { get; private init; }
    public string Competence { get; private init; } = string.Empty;
    public DateOnly CycleStartDate { get; private init; }
    public DateOnly ClosingDate { get; private init; }
    public DateOnly DueDate { get; private init; }
    public DateTimeOffset CreatedAt { get; private init; }

    public static FinancialCreditCardInvoice Create(Guid familyId, Guid cardId, string competence, DateOnly cycleStartDate, DateOnly closingDate, DateOnly dueDate, DateTimeOffset now) => new()
    {
        FamilyId = familyId, CreditCardId = cardId, Competence = competence, CycleStartDate = cycleStartDate,
        ClosingDate = closingDate, DueDate = dueDate, CreatedAt = now,
    };

    public FinancialCreditCardInvoiceStatus GetStatus(DateOnly today) => today > ClosingDate ? FinancialCreditCardInvoiceStatus.Closed : FinancialCreditCardInvoiceStatus.Open;
}

public sealed record CreditCardInvoiceCalendar(DateOnly CycleStartDate, DateOnly ClosingDate, DateOnly DueDate, string Competence);

public static class CreditCardInvoiceCalendarCalculator
{
    public static CreditCardInvoiceCalendar ForInstallment(DateOnly purchaseDate, int installmentNumber, int closingDay, int dueDay)
    {
        var firstClosing = ClosingOnOrAfter(purchaseDate, closingDay);
        var closing = AddMonths(firstClosing, installmentNumber - 1, closingDay);
        var previousClosing = AddMonths(closing, -1, closingDay);
        var due = DueAfter(closing, dueDay);
        return new CreditCardInvoiceCalendar(previousClosing.AddDays(1), closing, due, $"{due:yyyy-MM}");
    }

    public static DateOnly ClosingOnOrAfter(DateOnly date, int day)
    {
        var candidate = DateOnly.FromDateTime(new DateTime(date.Year, date.Month, Math.Min(day, DateTime.DaysInMonth(date.Year, date.Month))));
        return candidate >= date ? candidate : AddMonths(candidate, 1, day);
    }

    public static DateOnly AddMonths(DateOnly date, int months, int day)
    {
        var first = new DateOnly(date.Year, date.Month, 1).AddMonths(months);
        return new DateOnly(first.Year, first.Month, Math.Min(day, DateTime.DaysInMonth(first.Year, first.Month)));
    }

    public static DateOnly DueAfter(DateOnly closing, int dueDay)
    {
        var candidate = new DateOnly(closing.Year, closing.Month, Math.Min(dueDay, DateTime.DaysInMonth(closing.Year, closing.Month)));
        return candidate > closing ? candidate : AddMonths(candidate, 1, dueDay);
    }
}
