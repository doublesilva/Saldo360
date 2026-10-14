using Saldo360.Api.Authentication;
using Saldo360.Application.Calendar;
using Saldo360.Application.Transactions;
using Saldo360.Domain.Categories;
using Saldo360.Domain.Common;

namespace Saldo360.Api.Calendar;

public static class FinancialCalendarEndpoints
{
    public static IEndpointRouteBuilder MapFinancialCalendarEndpoints(this IEndpointRouteBuilder app)
    {
        app.MapGet("/api/families/{familyId:guid}/financial-calendar", List).RequireAuthorization();
        return app;
    }

    /// <summary>
    /// Agenda cronológica inclusiva. from/through são obrigatórios em yyyy-MM-dd; accountId e type
    /// (Income/Expense) são opcionais. Realizações aparecem somente pelo lançamento efetivo.
    /// </summary>
    private static async Task<IResult> List(
        Guid familyId,
        string? from,
        string? through,
        Guid? accountId,
        string? type,
        CurrentUser currentUser,
        FinancialCalendarService calendar,
        CancellationToken cancellationToken)
    {
        if (await currentUser.GetUserIdAsync(cancellationToken) is not { } userId)
        {
            return Results.Unauthorized();
        }

        var errors = new Dictionary<string, string[]>();
        var fromDate = ParseRequired(from, "from", "Informe a data inicial.", errors);
        var throughDate = ParseRequired(through, "through", "Informe a data final.", errors);
        if (errors.Count > 0)
        {
            return Results.ValidationProblem(errors);
        }

        var result = await calendar.ListAsync(
            familyId,
            userId,
            new FinancialCalendarQuery(fromDate!.Value, throughDate!.Value, accountId, type),
            cancellationToken);
        return result switch
        {
            { NotFound: true } => Results.NotFound(),
            { Errors: { } validationErrors } => Results.ValidationProblem(validationErrors),
            _ => Results.Ok(FinancialCalendarResponse.Create(result.Calendar!)),
        };
    }

    private static DateOnly? ParseRequired(
        string? value,
        string field,
        string requiredMessage,
        Dictionary<string, string[]> errors)
    {
        if (string.IsNullOrEmpty(value))
        {
            errors[field] = [requiredMessage];
            return null;
        }

        return IsoDateQuery.Parse(value, field, errors);
    }
}

public sealed record FinancialCalendarResponse(
    DateOnly From,
    DateOnly Through,
    IReadOnlyList<FinancialCalendarItemResponse> Items)
{
    public static FinancialCalendarResponse Create(FinancialCalendar calendar) =>
        new(calendar.From, calendar.Through, calendar.Items.Select(FinancialCalendarItemResponse.From).ToList());
}

public sealed record FinancialCalendarItemResponse(
    Guid Id,
    FinancialCalendarSource Source,
    FinancialCalendarStatus Status,
    CategoryType Type,
    string Amount,
    DateOnly Date,
    string Description,
    TransactionReferenceDto Account,
    TransactionReferenceDto Category,
    string Currency,
    Guid? PlannedTransactionId)
{
    public static FinancialCalendarItemResponse From(FinancialCalendarItemDto item) => new(
        item.Id,
        item.Source,
        item.Status,
        item.Type,
        Money.Format(item.Amount),
        item.Date,
        item.Description,
        item.Account,
        item.Category,
        item.Currency,
        item.PlannedTransactionId);
}
