using Saldo360.Application.Bootstrap;
using Saldo360.Domain.Accounts;
using Saldo360.Domain.Budgets;
using Saldo360.Domain.Categories;
using Saldo360.Domain.Common;
using Saldo360.Domain.CreditCards;
using Saldo360.Domain.Planning;
using Saldo360.Domain.Recurrences;

namespace Saldo360.Infrastructure.Bootstrap;

/// <summary>Validates/constructs the complete graph in memory. No DbContext, Add or SaveChanges.</summary>
public static class SpreadsheetDomainGraph
{
    public sealed record Result(IReadOnlyList<SpreadsheetPlanItem> Items, IReadOnlyDictionary<string, Entity> Entities);
    public static Result Build(SpreadsheetImportPlan plan, Guid owner, DateTimeOffset now,
        IReadOnlyDictionary<string, Entity>? existing = null)
    {
        var entities = existing is null ? new Dictionary<string, Entity>() : new Dictionary<string, Entity>(existing);
        var items = new List<SpreadsheetPlanItem>();
        foreach (var item in plan.Items)
        {
            if (!item.IsEntity || item.Status is not (BootstrapDisposition.Created or BootstrapDisposition.AlreadyImported)) { items.Add(item); continue; }
            try
            {
                Guid Ref(string? reference) => reference is not null && entities.TryGetValue(reference, out var e) ? e.Id : throw new ArgumentException("InvalidDependency");
                var p = item.Payload;
                if (string.IsNullOrWhiteSpace(p.Name) || p.Name.Length > 200 || p.Amount.HasValue && Money.Validate(p.Amount.Value, "Valor") is not null)
                    throw new ArgumentException("InvalidNameOrMoney");
                if (p.Category is not null && entities.GetValueOrDefault(p.Category) is not FinancialCategory { IsActive: true, Type: CategoryType.Expense }) throw new ArgumentException("InvalidCategoryDependency");
                if (p.Account is not null && entities.GetValueOrDefault(p.Account) is not FinancialAccount { IsActive: true }) throw new ArgumentException("InvalidAccountDependency");
                if (p.Card is not null && entities.GetValueOrDefault(p.Card) is not FinancialCreditCard { IsArchived: false }) throw new ArgumentException("InvalidCardDependency");
                Entity expected = item.EntityType switch
                {
                    SpreadsheetEntity.Category => Category(plan.WorkspaceId, owner, p, now),
                    SpreadsheetEntity.Account => FinancialAccount.Create(plan.WorkspaceId, AccountDetails.TryCreate(p.Name, p.Institution, "Checking", p.Scope, null, p.Amount, p.Date, out _) ?? throw new ArgumentException("InvalidAccount"), owner, now),
                    SpreadsheetEntity.Card => FinancialCreditCard.Create(plan.WorkspaceId, CreditCardDetails.TryCreate(p.Name, p.Institution, null, p.Amount, p.Day, p.Number, null, out _) ?? throw new ArgumentException("InvalidCardIssuerOrCalendar"), owner, now),
                    SpreadsheetEntity.PlannedTransaction => FinancialPlannedTransaction.Create(plan.WorkspaceId, PlannedTransactionDetails.TryCreate(Ref(p.Account), Ref(p.Category), "Expense", p.Name, p.Amount, p.Date, out _) ?? throw new ArgumentException("InvalidPlannedTransaction"), owner, now),
                    SpreadsheetEntity.AccountRecurrence or SpreadsheetEntity.CardRecurrence => Recurrence(plan.WorkspaceId, p, Ref, now),
                    SpreadsheetEntity.Purchase => Purchase(plan.WorkspaceId, owner, p, Ref, now),
                    SpreadsheetEntity.Invoice => Invoice(plan.WorkspaceId, p, Ref, now),
                    SpreadsheetEntity.Installment => Installment(p, entities, Ref, item.Status == BootstrapDisposition.AlreadyImported),
                    SpreadsheetEntity.KnownInstallment => FinancialCreditCardImportedInstallment.CreateKnown(plan.WorkspaceId, Ref(p.Card), Ref(p.Category), p.Name,
                        p.Amount!.Value, p.Number, p.Count, p.Competence!.Value, Due(p), SpreadsheetSnapshot.System, item.ExternalReference, owner, now),
                    SpreadsheetEntity.MonthlyAggregateTarget => FinancialMonthlyAggregateTarget.Create(plan.WorkspaceId, p.Competence!.Value, p.TargetType!.Value, p.Amount!.Value, now),
                    _ => throw new ArgumentException("UnsupportedEntity")
                };
                if (item.Status == BootstrapDisposition.AlreadyImported)
                {
                    if (!entities.TryGetValue(item.ExternalReference, out var current) || !Matches(expected, current))
                        throw new ArgumentException("DestinationEntityChangedOrMissing");
                }
                else entities.Add(item.ExternalReference, expected);
                items.Add(item);
            }
            catch (Exception e) when (e is ArgumentException or InvalidOperationException or NullReferenceException or KeyNotFoundException or OverflowException)
            {
                entities.Remove(item.ExternalReference);
                items.Add(item with { Status = item.Status == BootstrapDisposition.AlreadyImported ? BootstrapDisposition.Conflict : BootstrapDisposition.Invalid,
                    Reason = e is ArgumentException && e.Message.All(c => char.IsAsciiLetter(c)) ? e.Message : "InvalidDomainGraphOrReference" });
            }
        }
        return new(items, entities);
    }

    private static FinancialCategory Category(Guid w, Guid actor, SpreadsheetPayload p, DateTimeOffset now)
    {
        if (!FinancialCategory.TryNormalizeName(p.Name, out var normalized, out _)) throw new ArgumentException("InvalidCategory");
        return FinancialCategory.Create(w, normalized, CategoryType.Expense, actor, now);
    }
    private static FinancialRecurrence Recurrence(Guid w, SpreadsheetPayload p, Func<string?, Guid> reference, DateTimeOffset now)
    {
        if (p.Date is null || p.Day is null or < 1 or > 31 || p.EndDate < p.Date || p.Amount is null or <= 0 || (p.Account is null) == (p.Card is null))
            throw new ArgumentException("InvalidRecurrenceCalendarOrDestination");
        if (p.Card is not null && CreditCardInvoiceCalendarCalculator.ForInstallment(p.Date.Value, 1, 5, 10).Competence != p.Competence?.ToString("yyyy-MM"))
            throw new ArgumentException("InvoiceCompetenceMismatch");
        return FinancialRecurrence.Create(w, new(p.Account is null ? null : reference(p.Account), reference(p.Category), CategoryType.Expense, p.Name, p.Amount.Value,
            p.IsEstimated, RecurrenceFrequency.Monthly, 1, p.Date.Value, p.EndDate, null, p.Day, p.Card is null ? null : reference(p.Card)), now);
    }
    private static FinancialCreditCardPurchase Purchase(Guid w, Guid actor, SpreadsheetPayload p, Func<string?, Guid> reference, DateTimeOffset now)
    {
        if (CreditCardPurchaseDetails.Validate(p.Name, p.Amount, p.Count, out _) is not null || p.Date is null) throw new ArgumentException("InvalidPurchase");
        return FinancialCreditCardPurchase.Create(w, reference(p.Card), reference(p.Category), p.Name, p.Date.Value, p.Amount!.Value, p.Count!.Value, actor, now);
    }
    private static FinancialCreditCardInvoice Invoice(Guid w, SpreadsheetPayload p, Func<string?, Guid> reference, DateTimeOffset now)
    {
        var calendar = CreditCardInvoiceCalendarCalculator.ForInstallment(p.Date!.Value, p.Number!.Value, 5, 10);
        if (calendar.Competence != p.Competence?.ToString("yyyy-MM")) throw new ArgumentException("InvoiceCompetenceMismatch");
        return FinancialCreditCardInvoice.Create(w, reference(p.Card), calendar.Competence, calendar.CycleStartDate, calendar.ClosingDate, calendar.DueDate, now);
    }
    private static FinancialCreditCardInstallment Installment(SpreadsheetPayload p, Dictionary<string, Entity> entities, Func<string?, Guid> reference, bool existing)
    {
        var purchase = (FinancialCreditCardPurchase)entities[p.Purchase!];
        var child = purchase.Installments.Single(x => x.InstallmentNumber == p.Number);
        if (child.Amount != p.Amount || purchase.CreditCardId != reference(p.Card) || purchase.CategoryId != reference(p.Category)
            || CreditCardInvoiceCalendarCalculator.ForInstallment(purchase.PurchaseDate, child.InstallmentNumber, 5, 10).Competence != p.Competence?.ToString("yyyy-MM")) throw new ArgumentException("InstallmentScheduleMismatch");
        // Do not mutate an existing tracked/read entity in dry-run. Existing invoice is checked below.
        var invoiceId = reference(p.Invoice);
        if ((existing || child.InvoiceId.HasValue) && child.InvoiceId != invoiceId) throw new ArgumentException("InstallmentInvoiceMismatch");
        if (!child.InvoiceId.HasValue) child.AssignInvoice(invoiceId);
        return child;
    }
    private static DateOnly Due(SpreadsheetPayload p) => new(p.Competence!.Value.Year, p.Competence.Value.Month, Math.Min(p.Day!.Value, DateTime.DaysInMonth(p.Competence.Value.Year, p.Competence.Value.Month)));

    private static bool Matches(Entity expected, Entity current) => (expected, current) switch
    {
        (FinancialCategory e, FinancialCategory c) => c.IsActive && c.Type == e.Type && c.Name.Equals(e.Name, StringComparison.OrdinalIgnoreCase),
        (FinancialAccount e, FinancialAccount c) => c.IsActive && (c.Name, c.Institution, c.Type, c.Scope, c.OpeningBalance, c.OpeningBalanceDate, c.ResponsibleUserId, c.Currency) == (e.Name, e.Institution, e.Type, e.Scope, e.OpeningBalance, e.OpeningBalanceDate, e.ResponsibleUserId, e.Currency),
        (FinancialCreditCard e, FinancialCreditCard c) => !c.IsArchived && (c.Name, c.Issuer, c.CreditLimit, c.ClosingDay, c.DueDay, c.HolderUserId, c.DefaultPaymentAccountId, c.LastFourDigits) == (e.Name, e.Issuer, e.CreditLimit, e.ClosingDay, e.DueDay, e.HolderUserId, e.DefaultPaymentAccountId, e.LastFourDigits),
        (FinancialPlannedTransaction e, FinancialPlannedTransaction c) => !c.IsCancelled && (c.AccountId, c.CategoryId, c.Type, c.Description, c.Amount, c.DueDate) == (e.AccountId, e.CategoryId, e.Type, e.Description, e.Amount, e.DueDate),
        (FinancialRecurrence e, FinancialRecurrence c) => c.Status == RecurrenceStatus.Active && (c.FinancialAccountId, c.FinancialCreditCardId, c.FinancialCategoryId, c.Description, c.Amount, c.IsEstimated, c.StartDate, c.EndDate, c.DayOfMonth, c.Frequency, c.Interval, c.OccurrenceCount) == (e.FinancialAccountId, e.FinancialCreditCardId, e.FinancialCategoryId, e.Description, e.Amount, e.IsEstimated, e.StartDate, e.EndDate, e.DayOfMonth, e.Frequency, e.Interval, e.OccurrenceCount),
        (FinancialCreditCardPurchase e, FinancialCreditCardPurchase c) => c.Status == CreditCardPurchaseStatus.Active && (c.CreditCardId, c.CategoryId, c.Description, c.PurchaseDate, c.TotalAmount, c.InstallmentCount) == (e.CreditCardId, e.CategoryId, e.Description, e.PurchaseDate, e.TotalAmount, e.InstallmentCount),
        (FinancialCreditCardInvoice e, FinancialCreditCardInvoice c) => (c.CreditCardId, c.Competence, c.CycleStartDate, c.ClosingDate, c.DueDate) == (e.CreditCardId, e.Competence, e.CycleStartDate, e.ClosingDate, e.DueDate),
        (FinancialCreditCardInstallment e, FinancialCreditCardInstallment c) => c.Status == CreditCardInstallmentStatus.Active && (c.PurchaseId, c.InstallmentNumber, c.Amount, c.InvoiceId) == (e.PurchaseId, e.InstallmentNumber, e.Amount, e.InvoiceId),
        (FinancialCreditCardImportedInstallment e, FinancialCreditCardImportedInstallment c) => c.Status == FinancialCreditCardImportedInstallmentStatus.Active && c.InvoiceId is null && (c.CreditCardId, c.CategoryId, c.Description, c.Amount, c.InstallmentNumber, c.InstallmentCount, c.Competence, c.DueDate, c.SourceSystem, c.ExternalReference) == (e.CreditCardId, e.CategoryId, e.Description, e.Amount, e.InstallmentNumber, e.InstallmentCount, e.Competence, e.DueDate, e.SourceSystem, e.ExternalReference),
        (FinancialMonthlyAggregateTarget e, FinancialMonthlyAggregateTarget c) => (c.Competence, c.TargetType, c.Amount) == (e.Competence, e.TargetType, e.Amount),
        _ => false
    };
}
