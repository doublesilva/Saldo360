using Saldo360.Application.Dashboard.PlanActual;
namespace Saldo360.Application.MonthlyPlan;

// PROJ-01: contratos da previsão de receita PJ e da projeção integrada de seis meses. Dinheiro trafega como texto decimal exato; nenhuma fórmula existe no navegador.

public sealed record IncomeForecastAccountOptionDto(Guid Id, string Name, string Scope);

public sealed record IncomeForecastMonthDto(
    string WorkedCompetence, DateOnly PaymentDate, int BusinessDays, int BillableDays, string GrossAmount, string TaxReserve, string Status, string RealizedAmount,
    bool DaysAdjusted, bool AmountAdjusted, bool DateAdjusted);

/// <summary>
/// Configuração da previsão. <c>Exists</c> falso = rascunho com os valores iniciais sugeridos (nada gravado, nada projetado). <c>IsActive</c> é a aprovação: só então a
/// estimativa entra no fluxo projetado.
/// </summary>
public sealed record IncomeForecastSettingsDto(
    bool Exists, bool IsActive, Guid? AccountId, string? AccountName, string HourlyRate, string HoursPerDay, int PaymentDay, int PaymentOffsetMonths, string TaxReservePercent,
    string OperationalReserve, IReadOnlyList<IncomeForecastMonthDto> Months, IReadOnlyList<IncomeForecastAccountOptionDto> Accounts);

public sealed record IncomeForecastRequest(
    Guid? AccountId, string? HourlyRate, string? HoursPerDay, int? PaymentDay, int? PaymentOffsetMonths, string? TaxReservePercent, string? OperationalReserve, bool IsActive);

/// <summary>Ajuste de uma competência trabalhada; campos nulos seguem o cálculo. <c>IsDismissed</c> descarta a previsão daquele recebimento.</summary>
public sealed record IncomeForecastMonthRequest(int? BillableDays, string? ExpectedAmount, DateOnly? PaymentDate, bool IsDismissed);

// ---- Projeção integrada ----------------------------------------------------------------------------------------------------------------------------

/// <summary><c>Source</c>: Forecast (estimativa ativada), Realized (receita realizada substitui), Dismissed, NotConfigured (sem configuração aprovada) ou None (sem recebimento no mês).</summary>
public sealed record IntegratedIncomeDto(
    string Source, string? WorkedCompetence, int? BusinessDays, int? BillableDays, string? HoursPerDay, string? HourlyRate, string? ExpectedGross, DateOnly? PaymentDate,
    string RealizedIncome, bool Adjusted);

/// <summary>
/// Disponibilidade PJ. <c>Closing</c> é o saldo bancário projetado (já líquido de despesas pendentes e repasses previstos); <c>Available</c> = Closing − tributos − reserva
/// operacional. A reserva de tributos nunca é debitada do saldo bancário e não se aplica quando há obrigação tributária cadastrada (<c>TaxObligationRegistered</c>).
/// </summary>
public sealed record IntegratedPjDto(
    string? Opening, string PendingIncome, string PendingExpense, string TaxReserve, bool TaxObligationRegistered, string OperationalReserve, string? Closing, string? Available,
    string? SuggestedTransfer);

public sealed record IntegratedTransfersDto(string PlannedPjToPf, string RealizedPjToPf);

public sealed record IntegratedPfDto(
    string? Opening, string? Closing, string Entries, string Exits, string Result, string FixedPending, string CardsOutstanding, string OtherPending, string TransfersIn, string PendingBankUncovered = "0.00", string PendingBankAmbiguous = "0.00");

/// <summary><c>TargetState</c>: Fixed (meta já gravada, preservada), Provisional (calculada na leitura, nada gravado) ou None (conta sem uso do limite na abertura).</summary>
public sealed record IntegratedOverdraftDto(
    string? Limit, string UsedStart, string UsedEnd, string? Available, string Excess, string Recovery, string? Target, string TargetState, string? AchievementPercent);

public sealed record IntegratedMonthDto(
    string Competence, string PeriodKind, IntegratedIncomeDto Income, IntegratedPjDto Pj, IntegratedTransfersDto Transfers, IntegratedPfDto Pf, IntegratedOverdraftDto? Overdraft, CardTargetDto Cards,
    bool Complete, IReadOnlyList<string> Quality);

/// <summary><c>RecoveryOverPeriod</c> = limite usado na abertura do primeiro mês − limite usado no fechamento projetado do último (negativo = piora); nulo sem saldos.</summary>
public sealed record IntegratedProjectionDto(
    bool IncomeForecastActive, Guid? PjAccountId, string? PjAccountName, Guid? PfAccountId, string? PfAccountName, string? RecoveryOverPeriod, IReadOnlyList<IntegratedMonthDto> Months);
