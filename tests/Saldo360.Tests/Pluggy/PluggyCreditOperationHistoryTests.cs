using Saldo360.Application.Pluggy;
using Saldo360.Domain.Pluggy;

namespace Saldo360.Tests.Pluggy;

/// <summary>T13.3: débito de operação de crédito só dispensa a revisão quando a própria série (valor, descrição, dia, conta) já foi decidida como despesa pelo usuário. Caso real: Solar, R$ 748,31 por 11 meses.</summary>
[Trait("Category", "CORE")]
[Trait("Area", "Pluggy")]
public sealed class PluggyCreditOperationHistoryTests
{
    private static readonly Guid Account = Guid.NewGuid();
    private static readonly DateOnly Today = new(2026, 11, 6);

    private static CreditOperationEvidence Item(DateOnly date, decimal amount = 748.31m, string description = "Saída CREDIARIO 0123", PriorTreatment treatment = PriorTreatment.UserExpense,
        bool debit = true, Guid? account = null, string operation = "OPERACAO_CREDITO") =>
        new(Guid.NewGuid(), account ?? Account, operation, amount, description, date, debit, treatment);

    private static List<CreditOperationEvidence> Series(int months, PriorTreatment treatment = PriorTreatment.UserExpense) =>
        Enumerable.Range(1, months).Select(m => Item(Today.AddMonths(-m).AddDays(m % 3), treatment: treatment)).ToList();

    private static PluggyResolution? Learn(CreditOperationEvidence candidate, IEnumerable<CreditOperationEvidence> history) =>
        PluggyCreditOperationHistory.Learn(candidate, [.. history, candidate]);

    [Fact]
    public void ASeriesDecidedAsExpenseByTheUserInThreeOrMoreMonthsIsAppliedAsExpense() =>
        Assert.Equal(PluggyResolution.AsExpense, Learn(Item(Today), Series(11)));

    [Fact]
    public void ALearnedMonthCountsForTheNextOneButNeverWithoutHumanDecisionsInTheHistory()
    {
        var history = Series(2).Concat(Series(3, PriorTreatment.LearnedExpense).Select(h => h with { Date = h.Date.AddMonths(-3) })).ToList();

        Assert.Equal(PluggyResolution.AsExpense, Learn(Item(Today), history));
        Assert.Null(Learn(Item(Today), Series(5, PriorTreatment.LearnedExpense)));   // sem âncora humana
    }

    [Theory]
    [InlineData(2)]   // série curta demais
    public void ATooShortSeriesStaysInReview(int months) => Assert.Null(Learn(Item(Today), Series(months)));

    [Fact]
    public void AnyUnresolvedOrDifferentlyTreatedItemOfTheSameSeriesKeepsItAmbiguous()
    {
        var history = Series(6).Append(Item(Today.AddMonths(-7), treatment: PriorTreatment.Other)).ToList();

        Assert.Null(Learn(Item(Today), history));
    }

    [Fact]
    public void ACreditAnotherAmountAnotherDescriptionAnotherAccountOrAnotherOperationIsNeverLearned()
    {
        var history = Series(8);

        Assert.Null(Learn(Item(Today, debit: false), history));                                 // entrada: poderia ser empréstimo recebido
        Assert.Null(Learn(Item(Today, amount: 748.32m), history));                              // valor diferente
        Assert.Null(Learn(Item(Today, description: "Saída OUTRA COISA 9"), history));           // descrição diferente
        Assert.Null(Learn(Item(Today, account: Guid.NewGuid()), history));                      // outra conta
        Assert.Null(Learn(Item(Today, operation: "PIX"), history));                             // não é operação de crédito
        Assert.Null(Learn(Item(Today.AddDays(12)), history));                                   // dia do mês fora da tolerância
    }

    [Fact]
    public void ReprocessingTheSameCandidateGivesTheSameDecision()
    {
        var candidate = Item(Today); var history = Series(11);

        Assert.Equal(Learn(candidate, history), Learn(candidate, history));
        Assert.Equal(PluggyResolution.AsExpense, PluggyCreditOperationHistory.Learn(candidate, [.. history, candidate with { Treatment = PriorTreatment.LearnedExpense }]));   // o próprio item nunca conta como histórico
    }
}
