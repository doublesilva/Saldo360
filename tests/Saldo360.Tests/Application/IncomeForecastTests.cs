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

    private static readonly DateOnly Today = new(2026, 11, 4);

    private static IncomeForecastOccurrence Occur(DateOnly worked, IncomeForecastSettings? settings = null, IncomeForecastMonthOverride? adjust = null, decimal linked = 0m, decimal unidentified = 0m, DateOnly? today = null)
    {
        var s = settings ?? Settings();
        return IncomeForecastCalculator.Occurrence(s, worked, IncomeForecastCalculator.BusinessDays(worked, []), adjust, new IncomeReceiptEvidence(linked, unidentified), today ?? Today);
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

    // Esperado (outubro trabalhado, recebimento em 10/11): 16.800,00; reserva de tributos 11%.
    private static readonly DateOnly Worked = new(2026, 10, 1);

    [Fact]
    public void ALinkedPartialReceipt_LeavesTheResidualToReceive_WithoutAHalfThresholdReplacement()
    {
        var partial = Occur(Worked, linked: 9000m);

        Assert.Equal(IncomeForecastStatus.PartiallyReceived, partial.Status);
        Assert.Equal((9000m, 7800m), (partial.RealizedAmount, partial.Residual));
        Assert.True(partial.ProjectsCash);
        Assert.Equal(7800m, partial.CashAmount); // só o residual entra no caixa; os 9.000 já estão no saldo bancário
        Assert.Equal(1848m, partial.TaxReserve); // 11% sobre o esperado, uma única vez
        Assert.Equal(IncomeForecastStatus.PartiallyReceived, Occur(Worked, linked: 8400m).Status); // exatamente a metade: continua parcial
    }

    [Fact]
    public void AnUnrelatedReceipt_NeverSettlesTheForecast_AndIsReportedAsUnidentified()
    {
        var occurrence = Occur(Worked, unidentified: 16800m);

        Assert.Equal(IncomeForecastStatus.Forecast, occurrence.Status);
        Assert.Equal((0m, 16800m, 16800m), (occurrence.RealizedAmount, occurrence.Residual, occurrence.UnidentifiedAmount));
    }

    [Fact]
    public void AFullLinkedReceipt_SettlesOnlyThatForecast_AndAnExcessKeepsTheEffectiveAmount()
    {
        var full = Occur(Worked, linked: 16800m);
        var excess = Occur(Worked, linked: 17500m);

        Assert.Equal((IncomeForecastStatus.Realized, false, 0m), (full.Status, full.ProjectsCash, full.Residual));
        Assert.Equal((IncomeForecastStatus.Realized, 17500m, 700m, 0m), (excess.Status, excess.RealizedAmount, excess.Excess, excess.Residual)); // não limita ao previsto
        Assert.Equal(1925m, excess.TaxReserve); // 11% de 17.500
    }

    [Fact]
    public void AnOverdueResidual_StaysIdentified_ButNeverBecomesACashInflowToday()
    {
        var overdue = Occur(Worked, linked: 9000m, today: new DateOnly(2026, 11, 20)); // recebimento era 10/11
        var untilToday = Occur(Worked, today: new DateOnly(2026, 11, 10)); // no próprio dia ainda projeta

        Assert.Equal((7800m, true, false, 0m), (overdue.Residual, overdue.Overdue, overdue.ProjectsCash, overdue.CashAmount));
        Assert.Equal(overdue.PaymentDate, new DateOnly(2026, 11, 10)); // nenhuma data nova inventada
        Assert.True(untilToday.ProjectsCash);
    }

    [Fact]
    public void Classify_LinksOnlyPayersSeenInPriorMonths_AndIgnoresYieldsAndRefunds()
    {
        var prior = new[] { new IncomeReceipt(new DateOnly(2026, 10, 9), 15000m, "PIX RECEBIDO  CLIENTE A") };
        var month = new[]
        {
            new IncomeReceipt(new DateOnly(2026, 11, 10), 9000m, "pix recebido cliente a"),
            new IncomeReceipt(new DateOnly(2026, 11, 11), 3000m, "PIX RECEBIDO OUTRO"),
            new IncomeReceipt(new DateOnly(2026, 11, 12), 5m, "REND PAGO APLIC AUT MAIS"),
        };

        Assert.Equal(new IncomeReceiptEvidence(9000m, 3000m), IncomeForecastCalculator.Classify(month, prior));
        Assert.Equal(new IncomeReceiptEvidence(0m, 12000m), IncomeForecastCalculator.Classify(month, [])); // sem histórico, nada vincula
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
