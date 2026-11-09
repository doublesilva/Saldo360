using Saldo360.Application.MonthlyPlan;

namespace Saldo360.Tests.Application;

/// <summary>Entrega 2: complementação até os tetos, alimentação e recuperação do cheque especial (puros, sem banco).</summary>
[Trait("Category", "CORE")]
[Trait("Area", "Planning")]
public sealed class ProjectionScenariosTests
{
    private static DateOnly M(int month) => new(2026, month, 1);

    private static ScenarioMonthFacts Facts(int month, decimal? opening, decimal? closing, decimal cards, decimal outside, decimal? cardTarget = 5000m, decimal? total = 12500m, int? payMonth = null) =>
        new(M(month), opening, closing, cards, outside, cardTarget, total, payMonth is { } p ? M(p) : null);

    [Fact]
    public void TheCeilingsScenarioComplementsOnlyTheDifference_AndNeverReducesWhatExceedsTheCeiling()
    {
        Assert.Equal((1800m, 0m), ProjectionScenarios.Complement(5000m, 3200m));
        Assert.Equal((0m, 800m), ProjectionScenarios.Complement(5000m, 5800m)); // R$ 5.800 conhecidos: preserva e sinaliza excesso de R$ 800

        var months = ProjectionScenarios.Ceilings([Facts(11, -1000m, -1000m, 3200m, 9000m, payMonth: 12), Facts(12, -1000m, -1000m, 0m, 0m)]);

        Assert.Equal((7500m, 0m, 1500m, 1800m, 0m), (months[0].OutsideTarget, months[0].OutsideComplement, months[0].OutsideExcess, months[0].CardComplement, months[0].CardExcess));
    }

    [Fact]
    public void TheCardComplementReducesTheBalanceOnlyWhenTheInvoiceIsPaid_AndEveryLaterBalanceCarriesIt()
    {
        // Novembro: fora dos cartões 6.000 (complemento 1.500 em novembro); cartões 3.200 (complemento 1.800, fatura paga em dezembro).
        var months = ProjectionScenarios.Ceilings([Facts(11, 10000m, 9000m, 3200m, 6000m, payMonth: 12), Facts(12, 9000m, 8000m, 5000m, 7500m, payMonth: 1), Facts(1, 8000m, 7000m, 5000m, 7500m)]);

        Assert.Equal(9000m - 1500m, months[0].Closing);                 // só o fora-dos-cartões cai em novembro
        Assert.Equal(8000m - 1500m - 1800m, months[1].Closing);          // o cartão cai no pagamento da fatura, em dezembro
        Assert.Equal(7000m - 3300m, months[2].Closing);  // o acumulado dos complementos segue em todos os saldos posteriores
        Assert.Equal(months[0].Closing, months[1].Opening);              // o fechamento alimenta a abertura seguinte
    }

    [Fact]
    public void WithoutDefinedTargetsNothingIsComplemented_AndAnUnknownBalanceStaysUnknown()
    {
        var months = ProjectionScenarios.Ceilings([Facts(11, 100m, null, 3000m, 1000m, cardTarget: null, total: null), Facts(12, null, 50m, 0m, 0m)]);

        Assert.False(months[0].TargetsDefined);
        Assert.Equal((0m, 0m), (months[0].OutsideComplement, months[0].CardComplement)); // sem meta não é "meta zero"
        Assert.Null(months[0].Closing);
        Assert.Null(months[1].Closing); // desconhecido não vira zero nos meses seguintes
    }

    [Theory]
    [InlineData(2500, 500, 1500, 4000)]   // cabe: 2.000 − 500 = 1.500 de reserva ainda não coberta, e o espaço é 2.500
    [InlineData(4500, 500, 500, 5000)]    // o espaço (500) limita a reserva
    [InlineData(5000, 0, 0, 5000)]        // meta já consumida: a reserva não aumenta o consumo
    [InlineData(6855.41, 0, 0, 6855.41)]  // acima da meta: o gasto real é preservado, sem reserva por cima
    public void TheFoodReserveIsASubTargetOfTheCardTarget_NeverPushingConsumptionAboveIt(double cardBase, double knownFood, double expectedFood, double expectedTotal)
    {
        var food = ProjectionScenarios.FoodForecastWithinCardTarget(2000m, (decimal)knownFood, (decimal)cardBase, 5000m);
        Assert.Equal((decimal)expectedFood, food);
        Assert.Equal((decimal)expectedTotal, (decimal)cardBase + food);
    }

    [Fact]
    public void WithoutACardTargetOrWithAFullyConsumedReserveNothingIsApplied()
    {
        Assert.Equal(0m, ProjectionScenarios.FoodForecastWithinCardTarget(2000m, 100m, 1000m, null));
        Assert.Equal(0m, ProjectionScenarios.FoodForecastWithinCardTarget(2000m, 100m, 1000m, 0m));
        Assert.Equal(0m, ProjectionScenarios.FoodForecastWithinCardTarget(2000m, 2300m, 1000m, 5000m)); // alimentação já consumida por inteiro
    }

    [Theory]
    [InlineData(-3000, 2000, 3000, 2000)]   // de −3.000 para +2.000: recupera 3.000 e sobram 2.000 positivos (não 5.000)
    [InlineData(-3000, 0, 3000, 0)]
    [InlineData(-3000, -1000, 2000, 0)]
    [InlineData(-3000, -5000, -2000, 0)]    // piora: recuperação negativa
    [InlineData(500, 800, 0, 800)]          // saldo positivo não usa limite
    public void OverdraftRecovery_IsTheReductionOfUsage_AndMayBeNegative(int opening, int closing, int recovery, int positive)
    {
        var view = ProjectionScenarios.Overdraft(opening, closing, opening, 16100m)!;

        Assert.Equal((recovery, positive), (view.MonthlyRecovery, view.PositiveBalance));
        Assert.Equal(Math.Max(0m, 16100m - Math.Max(0m, -closing)), view.Available);
    }

    [Fact]
    public void TheCumulativeRecoveryIsMeasuredFromTheReferencePosition_AndAnUnknownBalanceIsNotCalculated()
    {
        var view = ProjectionScenarios.Overdraft(-2000m, -500m, -9000m, 16100m)!;

        Assert.Equal((1500m, 8500m), (view.MonthlyRecovery, view.CumulativeRecovery));
        Assert.Null(ProjectionScenarios.Overdraft(null, -500m, -9000m, 16100m));
        Assert.Null(ProjectionScenarios.Overdraft(-2000m, -500m, null, 16100m));
        Assert.Null(ProjectionScenarios.Overdraft(-2000m, -500m, -9000m, null)!.Available);
    }

    [Fact]
    public void TheFoodProvisionStaysInsideTheCardCeiling_NeverAddedOnTopOfIt()
    {
        // Cartões conhecidos 3.200; alimentação: provisão 2.000, reconhecido 800 → estimativa de 1.200 (já dentro do teto de 5.000).
        var food = ProjectionScenarios.FoodForecastWithinCardTarget(2000m, 800m, 3200m, 5000m);
        var months = ProjectionScenarios.Ceilings([Facts(11, 10000m, 10000m, 3200m, 7500m, payMonth: 12) with { FoodComplement = food }, Facts(12, 10000m, 10000m, 5000m, 7500m, payMonth: 1)]);

        Assert.Equal(600m, months[0].CardComplement);                       // 5.000 − (3.200 + 1.200): só o que falta além da alimentação
        Assert.Equal(10000m, months[0].CurrentClosing);                     // a fatura de novembro só é paga em dezembro
        Assert.Equal(10000m - 1200m, months[1].CurrentClosing);             // Atual: só a estimativa de alimentação reduz o caixa
        Assert.Equal(10000m - 1200m - 600m, months[1].Closing);             // Tetos: alimentação + complemento = 1.800 (teto − conhecido), nunca 1.200 + 1.800
        Assert.Equal(months[0].CurrentClosing, months[1].CurrentOpening);
    }

    [Fact]
    public void KnownCardSpendingAboveTheCeilingIsKeptAndFlagged_WithoutAnyFoodOnTop()
    {
        var food = ProjectionScenarios.FoodForecastWithinCardTarget(2000m, 0m, 5700m, 5000m);
        var months = ProjectionScenarios.Ceilings([Facts(11, 0m, 0m, 5700m, 7500m, payMonth: 12) with { FoodComplement = food }]);

        Assert.Equal(0m, food);
        Assert.Equal((0m, 700m), (months[0].CardComplement, months[0].CardExcess)); // 5.700 conhecidos: preserva e sinaliza 700, sem reserva por cima
    }

    [Fact]
    public void TheCurrentAndCeilingScenariosCountTheFoodReserveOnlyOnce()
    {
        var food = ProjectionScenarios.FoodForecastWithinCardTarget(2000m, 500m, 2500m, 5000m); // 1.500, cabe
        var months = ProjectionScenarios.Ceilings([Facts(11, 10000m, 10000m, 2500m, 7500m, payMonth: 12) with { FoodComplement = food }, Facts(12, 10000m, 10000m, 0m, 7500m, payMonth: 1)]);

        Assert.Equal(1000m, months[0].CardComplement);                // 5.000 − (2.500 + 1.500)
        Assert.Equal(10000m - 1500m, months[1].CurrentClosing);       // Atual: só a previsão alimentar adicional, no mês do pagamento da fatura
        Assert.Equal(10000m - 1500m - 1000m, months[1].Closing);      // Tetos: soma até a meta (2.500 conhecidos + 2.500), a reserva não entra de novo
    }

    [Fact]
    public void ASubscriptionAlreadyInTheCardBaseShrinksTheFoodComplementWithoutCappingKnownSpendingOrCountingItTwice()
    {
        var withoutSubscription = ProjectionScenarios.FoodForecastWithinCardTarget(2000m, 500m, 2500m, 5000m);     // 1.500 de complemento
        var withSubscription = ProjectionScenarios.FoodForecastWithinCardTarget(2000m, 500m, 2500m + 39.90m, 5000m);  // a assinatura entra na base antes do complemento

        Assert.Equal(1500m, withoutSubscription);
        Assert.Equal(1500m, withSubscription);                                    // ainda cabe: o gasto conhecido não limita o complemento
        Assert.Equal(460.10m, ProjectionScenarios.FoodForecastWithinCardTarget(2000m, 0m, 4500m + 39.90m, 5000m));  // sem folga suficiente só o que falta até a meta, nunca soma por cima
    }
}
