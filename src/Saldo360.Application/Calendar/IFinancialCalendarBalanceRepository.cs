namespace Saldo360.Application.Calendar;

public interface IFinancialCalendarBalanceRepository
{
    /// <summary>
    /// Lê contas, movimentações realizadas e previsões pendentes agregadas por dia num único snapshot.
    /// null indica que a conta solicitada não pertence à família.
    /// </summary>
    Task<FinancialCalendarBalanceSnapshot?> ReadAsync(
        Guid familyId,
        Guid? accountId,
        DateOnly through,
        DateOnly today,
        CancellationToken cancellationToken);

    /// <summary>
    /// Lê a base de saldos e os movimentos do período no mesmo snapshot somente leitura.
    /// </summary>
    Task<FinancialCalendarBalanceSnapshot?> ReadMonthlyAsync(
        Guid familyId,
        Guid? accountId,
        DateOnly from,
        DateOnly through,
        DateOnly today,
        CancellationToken cancellationToken);
}
