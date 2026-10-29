using Saldo360.Domain.Common;

namespace Saldo360.Domain.CreditCards;

public enum FinancialCreditCardInvoiceStatus { Open, Closed }

public sealed class FinancialCreditCardInvoice : Entity
{
    private FinancialCreditCardInvoice() { }
    public Guid WorkspaceId { get; private init; }
    public Guid CreditCardId { get; private init; }
    public string Competence { get; private init; } = string.Empty;
    public DateOnly CycleStartDate { get; private init; }
    public DateOnly ClosingDate { get; private init; }
    public DateOnly DueDate { get; private init; }
    public DateTimeOffset CreatedAt { get; private init; }
    public int OfficialVersion { get; private set; }
    public decimal? OfficialTotal { get; private set; }
    public DateTimeOffset? OfficialClosedAt { get; private set; }
    /// <summary>Quando a instituição informou que esta fatura fechou (T09.3.2). Independe de conciliação e de fatura oficial.</summary>
    public DateTimeOffset? InstitutionClosedAt { get; private set; }
    /// <summary>Total da fatura como o banco o informou. Preservado como veio: nunca é recalculado nem ajustado para bater com as compras.</summary>
    public decimal? InstitutionTotal { get; private set; }
    public string? InstitutionSource { get; private set; }

    public const int InstitutionSourceMaxLength = 16;
    public bool IsClosedByInstitution => InstitutionClosedAt is not null;

    /// <summary>
    /// Registra o fechamento informado pela instituição e seu total. Idempotente: a primeira data é mantida e o total acompanha a última
    /// observação. Não cria consolidação oficial nem ajustes; a composição local pode divergir do total bancário e isso é só conciliação.
    /// </summary>
    public void RecordInstitutionClosure(decimal total, string source, DateTimeOffset now)
    {
        if (total < 0 || Money.Validate(total, "Fatura") is not null || string.IsNullOrWhiteSpace(source) || source.Length > InstitutionSourceMaxLength)
            throw new ArgumentException("Fechamento informado pela instituição inválido.");
        InstitutionTotal = total; InstitutionSource = source; InstitutionClosedAt ??= now;
    }

    public void CloseOfficial(int version, decimal total, DateTimeOffset now)
    {
        if (version != OfficialVersion + 1 || total < 0 || Money.Validate(total, "Fatura") is not null) throw new ArgumentException("Versão oficial inválida.");
        OfficialVersion = version; OfficialTotal = total; OfficialClosedAt = now;
    }

    public static FinancialCreditCardInvoice Create(Guid workspaceId, Guid cardId, string competence, DateOnly cycleStartDate, DateOnly closingDate, DateOnly dueDate, DateTimeOffset now) => new()
    {
        WorkspaceId = workspaceId, CreditCardId = cardId, Competence = competence, CycleStartDate = cycleStartDate,
        ClosingDate = closingDate, DueDate = dueDate, CreatedAt = now,
    };

    public FinancialCreditCardInvoiceStatus GetStatus(DateOnly today) => OfficialVersion > 0 || InstitutionClosedAt is not null ? FinancialCreditCardInvoiceStatus.Closed : FinancialCreditCardInvoiceStatus.Open;
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
