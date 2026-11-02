using Saldo360.Domain.Common;

namespace Saldo360.Domain.Planning;

/// <summary>
/// PROJ-01: configuração de previsão de receita por horas (PJ). É uma ESTIMATIVA PARAMETRIZADA: só entra no fluxo projetado quando <see cref="IsActive"/> (aprovada pelo
/// usuário) e nunca cria lançamento realizado. Uma por orçamento. Valor bruto da competência trabalhada = valor/hora × horas/dia × dias faturáveis; o recebimento cai no mês
/// da competência + <see cref="PaymentOffsetMonths"/>, no dia <see cref="PaymentDay"/>.
/// </summary>
public sealed class FinancialIncomeForecast : Entity
{
    private FinancialIncomeForecast() { }

    public Guid WorkspaceId { get; private init; }
    public Guid AccountId { get; private set; }
    public decimal HourlyRate { get; private set; }
    public decimal HoursPerDay { get; private set; }
    public int PaymentDay { get; private set; }
    public int PaymentOffsetMonths { get; private set; }
    public decimal TaxReservePercent { get; private set; }
    public decimal OperationalReserve { get; private set; }
    public bool IsActive { get; private set; }
    public DateTimeOffset CreatedAt { get; private init; }
    public DateTimeOffset UpdatedAt { get; private set; }
    public Guid UpdatedByUserId { get; private set; }

    public static FinancialIncomeForecast Create(Guid workspaceId, Guid accountId, decimal hourlyRate, decimal hoursPerDay, int paymentDay, int paymentOffsetMonths,
        decimal taxReservePercent, decimal operationalReserve, bool isActive, Guid userId, DateTimeOffset now)
    {
        if (workspaceId == Guid.Empty || userId == Guid.Empty) throw new ArgumentException("Orçamento ou usuário inválido.");
        var entity = new FinancialIncomeForecast { WorkspaceId = workspaceId, CreatedAt = now };
        entity.Revise(accountId, hourlyRate, hoursPerDay, paymentDay, paymentOffsetMonths, taxReservePercent, operationalReserve, isActive, userId, now);
        return entity;
    }

    public void Revise(Guid accountId, decimal hourlyRate, decimal hoursPerDay, int paymentDay, int paymentOffsetMonths, decimal taxReservePercent, decimal operationalReserve,
        bool isActive, Guid userId, DateTimeOffset now)
    {
        if (accountId == Guid.Empty || userId == Guid.Empty) throw new ArgumentException("Conta ou usuário inválido.");
        if (hourlyRate <= 0m || hoursPerDay is <= 0m or > 24m) throw new ArgumentException("Valor por hora ou horas por dia inválido.");
        if (paymentDay is < 1 or > 31 || paymentOffsetMonths is < 0 or > 3) throw new ArgumentException("Dia ou deslocamento de pagamento inválido.");
        if (taxReservePercent is < 0m or > 100m || operationalReserve < 0m) throw new ArgumentException("Reserva inválida.");
        AccountId = accountId; HourlyRate = hourlyRate; HoursPerDay = hoursPerDay; PaymentDay = paymentDay; PaymentOffsetMonths = paymentOffsetMonths;
        TaxReservePercent = taxReservePercent; OperationalReserve = operationalReserve; IsActive = isActive; UpdatedByUserId = userId; UpdatedAt = now;
    }
}

/// <summary>
/// PROJ-01: ajuste de UMA competência trabalhada (primeiro dia do mês): dias faturáveis, valor esperado, data de recebimento ou descarte da previsão. Campos nulos
/// seguem o cálculo da configuração. Não é lançamento nem previsão de caixa por si só.
/// </summary>
public sealed class FinancialIncomeForecastMonth : Entity
{
    private FinancialIncomeForecastMonth() { }

    public Guid WorkspaceId { get; private init; }
    public DateOnly Competence { get; private init; }
    public int? BillableDays { get; private set; }
    public decimal? ExpectedAmount { get; private set; }
    public DateOnly? PaymentDate { get; private set; }
    public bool IsDismissed { get; private set; }
    public DateTimeOffset UpdatedAt { get; private set; }
    public Guid UpdatedByUserId { get; private set; }

    public static FinancialIncomeForecastMonth Create(Guid workspaceId, DateOnly competence, int? billableDays, decimal? expectedAmount, DateOnly? paymentDate, bool isDismissed, Guid userId, DateTimeOffset now)
    {
        if (workspaceId == Guid.Empty || userId == Guid.Empty || competence.Day != 1) throw new ArgumentException("Orçamento, usuário ou competência inválido.");
        var entity = new FinancialIncomeForecastMonth { WorkspaceId = workspaceId, Competence = competence };
        entity.Revise(billableDays, expectedAmount, paymentDate, isDismissed, userId, now);
        return entity;
    }

    public void Revise(int? billableDays, decimal? expectedAmount, DateOnly? paymentDate, bool isDismissed, Guid userId, DateTimeOffset now)
    {
        if (billableDays is < 0 or > 31 || expectedAmount is < 0m) throw new ArgumentException("Dias faturáveis ou valor esperado inválido.");
        BillableDays = billableDays; ExpectedAmount = expectedAmount; PaymentDate = paymentDate; IsDismissed = isDismissed; UpdatedByUserId = userId; UpdatedAt = now;
    }
}
