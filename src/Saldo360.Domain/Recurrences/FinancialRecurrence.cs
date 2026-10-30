using Saldo360.Domain.Categories;
using Saldo360.Domain.Common;

namespace Saldo360.Domain.Recurrences;

public enum RecurrenceFrequency { Weekly, Monthly, Yearly }
public enum RecurrenceStatus { Active, Paused, Ended, Archived }

public sealed class FinancialRecurrence : Entity
{
    public const int DescriptionMaxLength = 200;
    private FinancialRecurrence() { Description = string.Empty; Currency = "BRL"; }

    public Guid WorkspaceId { get; private init; }
    public Guid? FinancialAccountId { get; private set; }
    public Guid? FinancialCreditCardId { get; private set; }
    public Guid FinancialCategoryId { get; private set; }
    public CategoryType Type { get; private set; }
    /// <summary>Natureza da despesa da definição; novas materializações a herdam (as já materializadas não são reclassificadas). Receita: sempre Undefined.</summary>
    public ExpenseNature Nature { get; private set; }
    public string Description { get; private set; }
    public decimal Amount { get; private set; }
    public bool IsEstimated { get; private set; }
    public RecurrenceFrequency Frequency { get; private set; }
    public int Interval { get; private set; }
    public DateOnly StartDate { get; private set; }
    public DateOnly? EndDate { get; private set; }
    public int? OccurrenceCount { get; private set; }
    public int DayOfMonth { get; private set; }
    public RecurrenceStatus Status { get; private set; }
    public string Currency { get; private init; }
    public DateTimeOffset CreatedAt { get; private init; }
    public DateTimeOffset UpdatedAt { get; private set; }
    public DateTimeOffset? ArchivedAt { get; private set; }

    public static FinancialRecurrence Create(Guid workspaceId, RecurrenceDetails details, DateTimeOffset now) =>
        new FinancialRecurrence { WorkspaceId = workspaceId, CreatedAt = now, UpdatedAt = now, Status = RecurrenceStatus.Active }.Apply(details);

    public void Update(RecurrenceDetails details, DateTimeOffset now)
    {
        if (Status == RecurrenceStatus.Archived) throw new InvalidOperationException("Recorrência arquivada não pode ser editada.");
        Apply(details); UpdatedAt = now;
    }
    public void Pause(DateTimeOffset now) { if (Status == RecurrenceStatus.Active) { Status = RecurrenceStatus.Paused; UpdatedAt = now; } }
    public void Resume(DateTimeOffset now) { if (Status == RecurrenceStatus.Paused) { Status = RecurrenceStatus.Active; UpdatedAt = now; } }
    public void End(DateTimeOffset now) { if (Status is not RecurrenceStatus.Archived) { Status = RecurrenceStatus.Ended; UpdatedAt = now; } }
    public void Archive(DateTimeOffset now) { if (Status != RecurrenceStatus.Archived) { Status = RecurrenceStatus.Archived; ArchivedAt = now; UpdatedAt = now; } }

    private FinancialRecurrence Apply(RecurrenceDetails d)
    {
        if (d.AccountId.HasValue == d.CreditCardId.HasValue || d.AccountId == Guid.Empty || d.CreditCardId == Guid.Empty)
            throw new ArgumentException("Informe exatamente um destino: conta ou cartão.");
        if (d.CreditCardId.HasValue && d.Type != CategoryType.Expense)
            throw new ArgumentException("Recorrência de cartão deve ser despesa.");
        FinancialAccountId = d.AccountId; FinancialCategoryId = d.CategoryId; Type = d.Type; Description = d.Description;
        FinancialCreditCardId = d.CreditCardId;
        Nature = d.Type == CategoryType.Expense ? d.Nature : ExpenseNature.Undefined;
        Amount = d.Amount; IsEstimated = d.IsEstimated; Frequency = d.Frequency; Interval = d.Interval;
        StartDate = d.StartDate; EndDate = d.EndDate; OccurrenceCount = d.OccurrenceCount;
        DayOfMonth = d.Frequency == RecurrenceFrequency.Weekly ? d.StartDate.Day : d.DayOfMonth ?? d.StartDate.Day;
        return this;
    }
}

public sealed record RecurrenceDetails(Guid? AccountId, Guid CategoryId, CategoryType Type, string Description, decimal Amount,
    bool IsEstimated, RecurrenceFrequency Frequency, int Interval, DateOnly StartDate, DateOnly? EndDate, int? OccurrenceCount, int? DayOfMonth, Guid? CreditCardId = null, ExpenseNature Nature = ExpenseNature.Undefined);
