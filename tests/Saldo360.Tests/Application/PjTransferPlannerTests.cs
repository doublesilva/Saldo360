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

    private static IReadOnlyDictionary<DateOnly, decimal> Closing(Func<DateOnly, decimal> balance)
    {
        var days = new Dictionary<DateOnly, decimal>();
        for (var day = Today; day <= Through; day = day.AddDays(1)) days[day] = balance(day);
        return days;
    }

    private static IncomeForecastOccurrence Receipt(DateOnly payment, decimal gross, decimal tax = 0m, string status = IncomeForecastStatus.Forecast) =>
        new(new DateOnly(payment.Year, payment.Month, 1).AddMonths(-1), payment, 20, 20, 0m, 0m, gross, tax, status, 0m, false, false, false);

    private static readonly IReadOnlySet<DateOnly> NoTaxMonths = new HashSet<DateOnly>();

    [Fact]
    public void ThePjSurplusIsTransferredEntirely_ButNeverTheReservesNorMoreThanTheLowestFutureBalance()
    {
        // PJ com R$ 2.000 hoje; receita de R$ 15.000 no dia 10 (tributo de 11% = 1.650); despesa de R$ 2.260 por mês no dia 20.
        var receiptDate = new DateOnly(2026, 11, 10);
        var closing = Closing(d => d < receiptDate ? 2000m : d < new DateOnly(2026, 11, 20) ? 17000m : 14740m + 0m);
        var plan = PjTransferPlanner.Plan(Today, Through, closing, [Receipt(receiptDate, 15000m, 1650m)], operationalReserve: 500m, NoTaxMonths, incomeInferred: false);

        Assert.Equal([Today, receiptDate], plan.Select(t => t.Date));
        Assert.Equal(1500m, plan[0].Amount);                       // hoje: 2.000 − reserva operacional 500 (a reserva de tributos ainda não existe)
        Assert.Equal(14740m - 1500m - 1650m - 500m, plan[1].Amount); // no dia 10: o menor saldo futuro (14.740) − já transferido − tributos − reserva
        Assert.All(plan, t => Assert.Equal(t.Amount, decimal.Round(t.Amount, 2, MidpointRounding.ToZero))); // centavos inteiros
    }

    [Fact]
    public void TheTransferNeverMakesThePjNegativeNorConsumesTheReserve_EvenWhenAFutureCommitmentExceedsTheCash()
    {
        var closing = Closing(d => d < new DateOnly(2026, 12, 1) ? 3000m : -200m); // compromisso futuro deixaria a PJ negativa
        var plan = PjTransferPlanner.Plan(Today, Through, closing, [], operationalReserve: 0m, NoTaxMonths, false);

        Assert.Empty(plan); // o mínimo futuro é negativo: nada sobra para transferir
    }

    [Fact]
    public void ARegisteredTaxObligationSuppressesTheTaxReserve_AndAReservePersistsIntoTheFollowingMonth()
    {
        var receiptDate = new DateOnly(2026, 11, 10);
        var closing = Closing(d => 10000m);
        var withReserve = PjTransferPlanner.Plan(Today, Through, closing, [Receipt(receiptDate, 15000m, 1650m)], 0m, NoTaxMonths, false);
        var registered = PjTransferPlanner.Plan(Today, Through, closing, [Receipt(receiptDate, 15000m, 1650m)], 0m, new HashSet<DateOnly> { new(2026, 11, 1) }, false);

        Assert.Equal(10000m, withReserve[0].Amount);   // hoje a reserva do recebimento ainda não existe
        Assert.Equal(Today, registered[0].Date);
        Assert.Equal(10000m, registered.Sum(t => t.Amount));
        Assert.Equal(10000m, withReserve.Sum(t => t.Amount)); // não há saldo adicional além do que já foi transferido hoje
    }

    [Fact]
    public void WithoutAKnownBalanceForEveryDayNothingIsProjected()
    {
        var closing = Closing(d => 5000m).Where(p => p.Key != new DateOnly(2026, 12, 25)).ToDictionary(p => p.Key, p => p.Value);

        Assert.Empty(PjTransferPlanner.Plan(Today, Through, closing, [], 0m, NoTaxMonths, false));
    }

    [Fact]
    public void ADismissedOrRealizedReceiptDoesNotTriggerATransfer_AndInferenceIsFlaggedOnlyAfterAnInferredReceipt()
    {
        var closing = Closing(d => d < new DateOnly(2026, 12, 10) ? 8000m : 23000m); // a previsão de 10/12 entra no saldo projetado
        var forecast = Receipt(new DateOnly(2026, 12, 10), 15000m, 1650m);
        var realized = Receipt(new DateOnly(2026, 11, 10), 15000m, 1650m, IncomeForecastStatus.Realized);
        var dismissed = Receipt(new DateOnly(2027, 1, 10), 15000m, 1650m, IncomeForecastStatus.Dismissed);

        var plan = PjTransferPlanner.Plan(Today, Through, closing, [realized, forecast, dismissed], 0m, NoTaxMonths, incomeInferred: true);

        Assert.Equal([Today, new DateOnly(2026, 12, 10)], plan.Select(t => t.Date));      // só hoje e a previsão ainda por vir disparam repasse
        Assert.Equal([false, true], plan.Select(t => t.DependsOnInferredIncome));         // o de hoje não depende de receita inferida; o seguinte, sim
        Assert.Equal([8000m, 23000m - 8000m - 3300m], plan.Select(t => t.Amount));                // reserva de tributos do recebimento realizado de novembro (carregada) + a do previsto
    }

    [Fact]
    public void ThePlanIsDeterministic_SoRepeatedReadsNeverProjectADifferentTransfer()
    {
        var closing = Closing(d => 7000m);
        var occurrences = new[] { Receipt(new DateOnly(2026, 12, 10), 15000m, 1650m) };

        Assert.Equal(PjTransferPlanner.Plan(Today, Through, closing, occurrences, 100m, NoTaxMonths, true), PjTransferPlanner.Plan(Today, Through, closing, occurrences, 100m, NoTaxMonths, true));
    }
}
