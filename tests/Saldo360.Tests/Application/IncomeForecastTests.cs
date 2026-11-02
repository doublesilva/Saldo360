using Saldo360.Application.MonthlyPlan;

namespace Saldo360.Tests.Application;

/// <summary>PROJ-01: fórmulas puras da receita PJ por horas, calendário de São Paulo, recebimento, reserva de tributos e disponibilidade (sem banco e sem relógio).</summary>
[Trait("Area", "Planning")]
[Trait("Category", "CORE")]
public sealed class IncomeForecastTests
{
    private static readonly Guid Account = Guid.NewGuid();
    private static IncomeForecastSettings Settings(int day = 10, int offset = 1, decimal tax = 11m, decimal rate = 100m, decimal hours = 8m) =>
        new(Account, rate, hours, day, offset, tax, 0m, true);

    private static IncomeForecastOccurrence Occur(DateOnly worked, IncomeForecastSettings? settings = null, IncomeForecastMonthOverride? adjust = null, decimal realized = 0m)
    {
        var s = settings ?? Settings();
        return IncomeForecastCalculator.Occurrence(s, worked, IncomeForecastCalculator.BusinessDays(worked, []), adjust, realized);
    }

    [Theory]
    [InlineData(2026, 10, 21)] // 22 dias de segunda a sexta − 12/10
    [InlineData(2026, 11, 19)] // 21 − 02/11 e 20/11 (15/11 é domingo)
    [InlineData(2026, 12, 22)] // 23 − 25/12
    [InlineData(2027, 1, 19)] // 21 − 01/01 e 25/01
    [InlineData(2027, 2, 20)] // sem feriado fixo (Carnaval é ponto facultativo: ajuste manual)
    [InlineData(2027, 3, 22)] // 23 − Sexta-feira Santa (26/03)
    [InlineData(2027, 4, 21)] // 22 − 21/04
    public void BusinessDays_UseTheSaoPauloCalendar(int year, int month, int expected) =>
        Assert.Equal(expected, IncomeForecastCalculator.BusinessDays(new DateOnly(year, month, 1), []));

    [Fact]
    public void GoodFriday_IsCalculatedFromEaster()
    {
        Assert.Contains(new DateOnly(2026, 4, 3), SaoPauloHolidays.For(2026).Keys);
        Assert.Contains(new DateOnly(2027, 3, 26), SaoPauloHolidays.For(2027).Keys);
        Assert.Contains(new DateOnly(2026, 7, 9), SaoPauloHolidays.For(2026).Keys); // estadual
        Assert.Contains(new DateOnly(2026, 1, 25), SaoPauloHolidays.For(2026).Keys); // aniversário da cidade
    }

    [Fact]
    public void UserNonWorkingDays_AddToTheBuiltInHolidays()
    {
        var november = new DateOnly(2026, 11, 1);
        Assert.Equal(18, IncomeForecastCalculator.BusinessDays(november, [new DateOnly(2026, 11, 10)]));
        Assert.Equal(19, IncomeForecastCalculator.BusinessDays(november, [new DateOnly(2026, 11, 20), new DateOnly(2026, 11, 14)])); // feriado repetido e sábado não contam em dobro
    }

    [Fact]
    public void GrossRevenue_FollowsTheBillableDaysOfEachCompetence()
    {
        Assert.Equal(16800m, Occur(new DateOnly(2026, 10, 1)).GrossAmount); // 21 × 8 × 100
        Assert.Equal(15200m, Occur(new DateOnly(2026, 11, 1)).GrossAmount); // 19
        Assert.Equal(17600m, Occur(new DateOnly(2026, 12, 1)).GrossAmount); // 22
        Assert.Equal(16000m, Occur(new DateOnly(2027, 2, 1)).GrossAmount); // 20
    }

    [Fact]
    public void WorkedMonthAndPaymentMonth_AreSeparated_ByTheConfiguredOffset()
    {
        var october = Occur(new DateOnly(2026, 10, 1));
        Assert.Equal((new DateOnly(2026, 10, 1), new DateOnly(2026, 11, 10)), (october.WorkedMonth, october.PaymentDate));
        var sameMonth = Occur(new DateOnly(2026, 10, 1), Settings(offset: 0));
        Assert.Equal(new DateOnly(2026, 10, 10), sameMonth.PaymentDate);
        Assert.Equal(new DateOnly(2027, 2, 28), IncomeForecastCalculator.PaymentDateFor(Settings(day: 31), new DateOnly(2027, 1, 1))); // dia 31 ajustado ao fim de fevereiro
    }

    [Fact]
    public void ManualAdjustments_ReplaceDaysAmountAndPaymentDate()
    {
        var worked = new DateOnly(2026, 11, 1);
        var days = Occur(worked, adjust: new(worked, 10, null, null, false));
        Assert.Equal((10, 8000m, true), (days.BillableDays, days.GrossAmount, days.DaysAdjusted));
        var amount = Occur(worked, adjust: new(worked, null, 12345.67m, new DateOnly(2026, 12, 15), false));
        Assert.Equal((12345.67m, new DateOnly(2026, 12, 15), true, true), (amount.GrossAmount, amount.PaymentDate, amount.AmountAdjusted, amount.DateAdjusted));
        Assert.Equal(IncomeForecastStatus.Dismissed, Occur(worked, adjust: new(worked, null, null, null, true)).Status);
    }

    [Fact]
    public void RealizedRevenue_ReplacesTheForecast_FromHalfOfTheExpectedAmount()
    {
        var worked = new DateOnly(2026, 10, 1); // esperado 16.800,00
        Assert.Equal(IncomeForecastStatus.Forecast, Occur(worked, realized: 8399.99m).Status);
        var replaced = Occur(worked, realized: 8400m);
        Assert.Equal(IncomeForecastStatus.Realized, replaced.Status);
        Assert.False(replaced.ProjectsCash); // nunca as duas: a estimativa deixa de projetar caixa
        Assert.Equal(924m, replaced.TaxReserve); // 11% do realizado
    }

    [Fact]
    public void TaxReserve_IsAPercentageOfTheGross_RoundedToCents_AndZeroWhenDismissed()
    {
        Assert.Equal(1848m, Occur(new DateOnly(2026, 10, 1)).TaxReserve); // 11% de 16.800
        Assert.Equal(1672m, Occur(new DateOnly(2026, 11, 1)).TaxReserve); // 11% de 15.200
        Assert.Equal(1.11m, IncomeForecastCalculator.TaxReserve(10.10m, 11m)); // 1,111 → 1,11
        Assert.Equal(0m, Occur(new DateOnly(2026, 10, 1), adjust: new(new DateOnly(2026, 10, 1), null, null, null, true)).TaxReserve);
    }

    [Fact]
    public void Availability_SubtractsReservesOnce_AndTheSuggestionIsNeverNegative()
    {
        // O saldo projetado já está líquido das despesas pendentes: só tributos e reserva operacional saem da disponibilidade.
        Assert.Equal(10652m, PjAvailability.Available(12500m, 1848m, 0m));
        Assert.Equal(9652m, PjAvailability.Available(12500m, 1848m, 1000m));
        Assert.Equal(-300m, PjAvailability.Available(1548m, 1848m, 0m));
        Assert.Equal(0m, PjAvailability.Suggested(-300m));
        Assert.Equal(10652.12m, PjAvailability.Suggested(10652.129m));
    }

    [Fact]
    public void WorkedMonths_CoverEveryPaymentInTheWindow()
    {
        var months = IncomeForecastCalculator.WorkedMonthsFor(Settings(), new DateOnly(2026, 11, 1), new DateOnly(2027, 4, 30)).ToList();
        Assert.Contains(new DateOnly(2026, 10, 1), months); // recebido em 10/11
        Assert.Contains(new DateOnly(2027, 3, 1), months); // recebido em 10/04
    }
}
