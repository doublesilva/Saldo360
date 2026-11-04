using Saldo360.Application.MonthlyPlan;

namespace Saldo360.Tests.Application;

/// <summary>PROJ-06: receita inferida do histórico e repasse PJ → PF projetado (puros, sem banco).</summary>
[Trait("Category", "CORE")]
[Trait("Area", "Planning")]
public sealed class PjTransferPlannerTests
{
    private static readonly DateOnly Current = new(2026, 11, 1);
    private static readonly DateOnly Today = new(2026, 11, 4);
    private static readonly DateOnly Through = new(2027, 4, 30);

    private static OperationalReceipt Receipt(int monthsAgo, decimal amount, string description = "PIX RECEBIDO CLIENTE", int day = 10) =>
        new(Current.AddMonths(-monthsAgo).AddDays(day - 1), amount, description);

    // ---- Receita inferida -----------------------------------------------------------------------------------------------------------------------

    [Fact]
    public void TheMedianOfTheLastSixValidMonthsIsInferred_ExcludingYieldsRefundsAndNonOperationalEntries()
    {
        var receipts = new List<OperationalReceipt>
        {
            Receipt(1, 15000m), Receipt(2, 16000m), Receipt(3, 17000m), Receipt(4, 15500m), Receipt(5, 16500m), Receipt(6, 15200m),
            Receipt(1, 300m, "REND PAGO APLIC AUT MAIS", 28), Receipt(2, 50m, "EST DEBITO SEGURO 04/48", 5), Receipt(3, 900m, "DEVOLUCAO COMPRA", 6), Receipt(4, 70m, "JUROS", 7),
            Receipt(7, 99999m), // fora da janela de seis meses
        };

        var inferred = IncomeInference.Infer(receipts, Current)!;

        Assert.Equal((15750.00m, 10, 6), (inferred.MonthlyAmount, inferred.PaymentDay, inferred.MonthsUsed)); // mediana de 15.000…17.000, dia típico 10
    }

    [Fact]
    public void WithFewerThanThreeValidMonthsNothingIsInferred()
    {
        Assert.Null(IncomeInference.Infer([Receipt(1, 15000m), Receipt(2, 16000m), Receipt(3, 300m, "REND PAGO APLIC AUT MAIS")], Current));
        Assert.Null(IncomeInference.Infer([], Current));
    }

    [Fact]
    public void TheTypicalPaymentDayIsTheMedianDayOfTheLargestReceiptOfEachMonth()
    {
        var receipts = new[] { Receipt(1, 10000m, day: 8), Receipt(1, 500m, day: 25), Receipt(2, 10000m, day: 10), Receipt(3, 10000m, day: 12) };

        Assert.Equal(10, IncomeInference.Infer(receipts, Current)!.PaymentDay);
    }

    // ---- Repasse PJ → PF ------------------------------------------------------------------------------------------------------------------------

    private static IReadOnlyDictionary<DateOnly, decimal> Closing(Func<DateOnly, decimal> balance, DateOnly? until = null)
    {
        var days = new Dictionary<DateOnly, decimal>();
        for (var day = Today; day <= (until ?? Through); day = day.AddDays(1)) days[day] = balance(day);
        return days;
    }

    private static IncomeForecastOccurrence Receipt(DateOnly payment, decimal gross, decimal tax = 0m, string status = IncomeForecastStatus.Forecast, bool overdue = false) =>
        new(new DateOnly(payment.Year, payment.Month, 1).AddMonths(-1), payment, 20, 20, 0m, 0m, gross, tax, status, 0m, false, false, false,
            status == IncomeForecastStatus.Forecast ? gross : 0m, 0m, overdue);

    private static readonly IReadOnlySet<DateOnly> NoTaxMonths = new HashSet<DateOnly>();
    private static readonly DateOnly Nov10 = new(2026, 11, 10);
    private static readonly DateOnly Dec10 = new(2026, 12, 10);

    [Fact]
    public void TheTransferHappensOnTheReceiptDay_NeverBecauseTodayWasIncluded_AndPreservesReservesAndPendingExpenses()
    {
        // PJ com R$ 2.000 hoje; receita de R$ 15.000 no dia 10 (tributo 1.650); despesa PJ pendente de 2.260 no dia 20; reserva operacional 500.
        var closing = Closing(d => d < Nov10 ? 2000m : d < new DateOnly(2026, 11, 20) ? 17000m : 14740m);

        var plan = PjTransferPlanner.Plan(Today, Through, closing, [Receipt(Nov10, 15000m, 1650m)], operationalReserve: 500m, NoTaxMonths, incomeInferred: false);

        var transfer = Assert.Single(plan); // nada em "hoje": não há gatilho financeiro
        Assert.Equal((Nov10, 14740m - 1650m - 500m), (transfer.Date, transfer.Amount)); // menor saldo da janela − tributos − reserva: a despesa de 2.260 já está no saldo
        Assert.Equal(transfer.Amount, decimal.Round(transfer.Amount, 2, MidpointRounding.ToZero));
    }

    [Fact]
    public void TheTransferNeverRelaysOnANegativePj_EvenWhenAKnownCommitmentExceedsTheCash()
    {
        var closing = Closing(d => d < new DateOnly(2026, 11, 25) ? 3000m : -200m); // compromisso conhecido deixaria a PJ negativa (sem limite contratado)

        Assert.Empty(PjTransferPlanner.Plan(Today, Through, closing, [Receipt(Nov10, 0.01m)], 0m, NoTaxMonths, false));
        Assert.Empty(PjTransferPlanner.Plan(Today, Through, Closing(d => 5000m), [], 0m, NoTaxMonths, false)); // sem recebimento, sem repasse (hoje não é gatilho)
    }

    [Fact]
    public void TheProtectionWindowEndsAtTheNextExpectedReceipt_SoTheResultIsTheSameForAnyHorizon()
    {
        // O saldo cai em 20/12, depois do próximo recebimento (10/12): essa queda é do repasse de dezembro, não do de novembro.
        Func<DateOnly, decimal> balance = d => d < Dec10 ? 9000m : d < new DateOnly(2026, 12, 20) ? 20000m : 1000m;
        var occurrences = new[] { Receipt(Nov10, 8000m), Receipt(Dec10, 11000m) };

        var shortHorizon = PjTransferPlanner.Plan(Today, new DateOnly(2026, 11, 30), Closing(balance, new DateOnly(2027, 1, 31)), occurrences, 0m, NoTaxMonths, false);
        var longHorizon = PjTransferPlanner.Plan(Today, new DateOnly(2027, 4, 30), Closing(balance, new DateOnly(2027, 5, 31)), occurrences, 0m, NoTaxMonths, false);

        Assert.Equal(9000m, shortHorizon.Single(t => t.Date == Nov10).Amount);
        Assert.Equal(shortHorizon.Single(t => t.Date == Nov10), longHorizon.Single(t => t.Date == Nov10)); // mesma competência, mesmo resultado em 6 ou 13 meses
        Assert.Equal([Nov10], shortHorizon.Select(t => t.Date)); // dezembro fora do horizonte curto
    }

    [Fact]
    public void ASecondTransferDiscountsWhatWasAlreadyTransferred_AndNeverCountsTheSameMoneyTwice()
    {
        var closing = Closing(d => d < Dec10 ? 9000m : 20000m);

        var plan = PjTransferPlanner.Plan(Today, Through, closing, [Receipt(Nov10, 8000m), Receipt(Dec10, 11000m)], 0m, NoTaxMonths, false);

        Assert.Equal([(Nov10, 9000m), (Dec10, 11000m)], plan.Select(t => (t.Date, t.Amount)));
    }

    [Fact]
    public void ARegisteredTaxObligationSuppressesTheTaxReserve()
    {
        var closing = Closing(d => 10000m);
        var withReserve = PjTransferPlanner.Plan(Today, Through, closing, [Receipt(Nov10, 15000m, 1650m)], 0m, NoTaxMonths, false);
        var registered = PjTransferPlanner.Plan(Today, Through, closing, [Receipt(Nov10, 15000m, 1650m)], 0m, new HashSet<DateOnly> { new(2026, 11, 1) }, false);

        Assert.Equal(10000m - 1650m, withReserve.Single().Amount);
        Assert.Equal(10000m, registered.Single().Amount);
    }

    [Fact]
    public void WithoutAKnownBalanceForEveryDayOfTheWindowNothingIsProjected()
    {
        var closing = Closing(d => 5000m).Where(p => p.Key != new DateOnly(2026, 12, 25)).ToDictionary(p => p.Key, p => p.Value);

        Assert.Empty(PjTransferPlanner.Plan(Today, Through, closing, [Receipt(Nov10, 1000m)], 0m, NoTaxMonths, false)); // janela de 10/11 a 31/12 inclui 25/12
    }

    [Fact]
    public void AnOverdueResidualOrADismissedReceiptDoesNotTriggerATransfer_AndInferenceIsFlagged()
    {
        var closing = Closing(d => 8000m);
        var overdue = Receipt(new DateOnly(2026, 11, 1), 15000m, 0m, overdue: true) with { Residual = 7000m };
        var dismissed = Receipt(new DateOnly(2027, 1, 10), 15000m, 0m, IncomeForecastStatus.Dismissed);

        Assert.Empty(PjTransferPlanner.Plan(Today, Through, closing, [overdue, dismissed], 0m, NoTaxMonths, true));
        Assert.True(PjTransferPlanner.Plan(Today, Through, closing, [Receipt(Nov10, 15000m)], 0m, NoTaxMonths, true).Single().DependsOnInferredIncome);
    }

    [Fact]
    public void ThePlanIsDeterministicAndIdempotent()
    {
        var closing = Closing(d => 7000m);
        var occurrences = new[] { Receipt(Dec10, 15000m, 1650m) };

        Assert.Equal(PjTransferPlanner.Plan(Today, Through, closing, occurrences, 100m, NoTaxMonths, true), PjTransferPlanner.Plan(Today, Through, closing, occurrences, 100m, NoTaxMonths, true));
    }
}
