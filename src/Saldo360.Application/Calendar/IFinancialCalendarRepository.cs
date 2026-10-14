namespace Saldo360.Application.Calendar;

public interface IFinancialCalendarRepository
{
    /// <summary>
    /// Reúne lançamentos ativos e previsões pendentes num único snapshot, em ordem cronológica estável.
    /// </summary>
    Task<FinancialCalendarSnapshot> ListAsync(
        Guid familyId,
        FinancialCalendarFilter filter,
        CancellationToken cancellationToken);
}
