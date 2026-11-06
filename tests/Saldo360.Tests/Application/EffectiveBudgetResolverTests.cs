using Saldo360.Application.Budgets;
using Saldo360.Domain.Budgets;

namespace Saldo360.Tests.Application;

/// <summary>T12.1: resolvedor de metas efetivas (precedência, vigência, exceções, retiradas e histórico). Puro: competências fixas, sem banco nem relógio.</summary>
[Trait("Category", "CORE")]
[Trait("Area", "Budgets")]
[Trait("Profile", "FAST")]
public sealed class EffectiveBudgetResolverTests
{
    private static readonly Guid Workspace = Guid.Parse("11111111-1111-1111-1111-111111111111");
    private static readonly Guid Food = Guid.NewGuid(), Market = Guid.NewGuid(), Groceries = Guid.NewGuid();
    private static DateOnly M(int year, int month) => new(year, month, 1);
    private static readonly DateTimeOffset Now = new(2026, 11, 1, 12, 0, 0, TimeSpan.Zero);

    private static RuleChangeFact Standard(BudgetRuleTarget target, DateOnly from, decimal? amount, int revision = 1, Guid? category = null, BudgetRuleAction? action = null) =>
        new(BudgetRuleScope.FromCompetence, target, category, from, revision, action ?? (amount is null ? BudgetRuleAction.Remove : BudgetRuleAction.Set), amount, Now);

    private static RuleChangeFact Exception(BudgetRuleTarget target, DateOnly month, decimal? amount, int revision = 1, Guid? category = null, BudgetRuleAction? action = null) =>
        new(BudgetRuleScope.OnlyCompetence, target, category, month, revision, action ?? (amount is null ? BudgetRuleAction.Remove : BudgetRuleAction.Set), amount, Now);

    private static BudgetLayers Layers(IEnumerable<RuleChangeFact>? changes = null, IEnumerable<LegacyMonthFact>? legacy = null, IReadOnlyDictionary<Guid, Guid?>? parents = null) =>
        new((changes ?? []).ToList(), (legacy ?? []).ToDictionary(l => l.Competence), parents ?? new Dictionary<Guid, Guid?>());

    private static EffectiveTarget Card(BudgetLayers layers, DateOnly month) => EffectiveBudgetResolver.Resolve(Workspace, layers, month).Find(BudgetRuleTarget.AllCards)!;

    // ---- Herança e vigência -----------------------------------------------------------------------------------------------------------------------------

    [Fact]
    public void MonthWithoutItsOwnConfigurationInheritsTheStandardInForceAndShowsSinceWhen()
    {
        var layers = Layers([Standard(BudgetRuleTarget.AllCards, M(2026, 11), 5000m), Standard(BudgetRuleTarget.TotalExpenses, M(2026, 11), 12500m)]);
        foreach (var month in new[] { M(2026, 11), M(2026, 12), M(2027, 6) })
        {
            var resolved = EffectiveBudgetResolver.Resolve(Workspace, layers, month);
            Assert.Equal(5000m, resolved.CardTarget); Assert.Equal(12500m, resolved.TotalExpenses);
            Assert.All(new[] { resolved.Find(BudgetRuleTarget.AllCards)!, resolved.Find(BudgetRuleTarget.TotalExpenses)! }, t => { Assert.Equal(TargetOrigin.Inherited, t.Origin); Assert.Equal(M(2026, 11), t.StandardFrom); });
        }
    }

    [Fact]
    public void MonthsBeforeTheStandardStartAreNeverFilledAndTheHistoricalOctoberStaysUntouched()
    {
        var october = new LegacyMonthFact(M(2026, 10), Guid.NewGuid(), [new(BudgetRuleTarget.AllCards, null, 4200m, false, Guid.NewGuid())], 11000m);
        var layers = Layers([Standard(BudgetRuleTarget.AllCards, M(2026, 11), 5000m)], [october]);
        var oct = EffectiveBudgetResolver.Resolve(Workspace, layers, M(2026, 10));
        Assert.Equal(4200m, oct.CardTarget); Assert.Equal(TargetOrigin.Historical, oct.Find(BudgetRuleTarget.AllCards)!.Origin);
        Assert.Equal(11000m, oct.TotalExpenses); Assert.Equal(october.BudgetId, oct.BudgetId);
        var september = EffectiveBudgetResolver.Resolve(Workspace, layers, M(2026, 9));
        Assert.Null(september.CardTarget); Assert.Equal(TargetOrigin.Undefined, september.Find(BudgetRuleTarget.AllCards)!.Origin);
        Assert.Equal(5000m, EffectiveBudgetResolver.Resolve(Workspace, layers, M(2026, 11)).CardTarget);
    }

    [Fact]
    public void WithoutAnyConfigurationEveryMonthIsUndefinedNeverZero()
    {
        var resolved = EffectiveBudgetResolver.Resolve(Workspace, Layers(), M(2026, 11));
        Assert.All(resolved.Targets, t => { Assert.Null(t.Amount); Assert.Equal(TargetOrigin.Undefined, t.Origin); Assert.False(t.Removed); });
        Assert.Equal(3, resolved.Targets.Count);
    }

    [Fact]
    public void AnExceptionOfNovemberDoesNotChangeDecemberAndNeverTouchesTheStandard()
    {
        var layers = Layers([Standard(BudgetRuleTarget.AllCards, M(2026, 11), 5000m), Exception(BudgetRuleTarget.AllCards, M(2026, 11), 6500m)]);
        var november = Card(layers, M(2026, 11));
        Assert.Equal(6500m, november.Amount); Assert.Equal(TargetOrigin.Customized, november.Origin);
        var december = Card(layers, M(2026, 12));
        Assert.Equal(5000m, december.Amount); Assert.Equal(TargetOrigin.Inherited, december.Origin);
    }

    [Fact]
    public void ChangingTheStandardFromJanuaryDoesNotChangeOctoberNovemberOrDecember()
    {
        var layers = Layers([Standard(BudgetRuleTarget.AllCards, M(2026, 11), 5000m), Standard(BudgetRuleTarget.AllCards, M(2027, 1), 5600m)]);
        Assert.Equal(5000m, Card(layers, M(2026, 11)).Amount); Assert.Equal(5000m, Card(layers, M(2026, 12)).Amount);
        Assert.Equal(5600m, Card(layers, M(2027, 1)).Amount); Assert.Equal(M(2027, 1), Card(layers, M(2027, 1)).StandardFrom);
        Assert.Equal(5600m, Card(layers, M(2027, 8)).Amount);
        Assert.Null(Card(layers, M(2026, 10)).Amount);
    }

    [Fact]
    public void TheHighestRevisionOfTheSameVigenciaWinsAndTheHistoryIsKept()
    {
        var first = Standard(BudgetRuleTarget.AllCards, M(2026, 11), 5000m, revision: 1); var second = Standard(BudgetRuleTarget.AllCards, M(2026, 11), 5300m, revision: 2);
        Assert.Equal(5300m, Card(Layers([first, second]), M(2026, 12)).Amount);
        Assert.Equal(5300m, Card(Layers([second, first]), M(2026, 12)).Amount); // a ordem de leitura não altera o resultado
    }

    // ---- Retiradas, restauração e não soma ---------------------------------------------------------------------------------------------------------------

    [Fact]
    public void ATargetRemovedOnPurposeInAMonthDoesNotComeBackThroughInheritance()
    {
        var layers = Layers([Standard(BudgetRuleTarget.AllCards, M(2026, 11), 5000m), Exception(BudgetRuleTarget.AllCards, M(2026, 12), null)]);
        var december = Card(layers, M(2026, 12));
        Assert.Null(december.Amount); Assert.True(december.Removed); Assert.Equal(TargetOrigin.Customized, december.Origin);
        Assert.Equal(5000m, Card(layers, M(2027, 1)).Amount); // a retirada vale só naquele mês
    }

    [Fact]
    public void RemovingTheStandardFromAMonthOnwardStopsInheritanceUntilItIsSetAgain()
    {
        var layers = Layers([Standard(BudgetRuleTarget.AllCards, M(2026, 11), 5000m), Standard(BudgetRuleTarget.AllCards, M(2027, 1), null), Standard(BudgetRuleTarget.AllCards, M(2027, 4), 4800m)]);
        Assert.Equal(5000m, Card(layers, M(2026, 12)).Amount);
        var january = Card(layers, M(2027, 1));
        Assert.Null(january.Amount); Assert.True(january.Removed); Assert.Equal(TargetOrigin.Inherited, january.Origin);
        Assert.Null(Card(layers, M(2027, 3)).Amount);
        Assert.Equal(4800m, Card(layers, M(2027, 4)).Amount);
    }

    [Fact]
    public void RestoringTheMonthReturnsItToTheStandardAndTheLatestRevisionDecides()
    {
        var set = Exception(BudgetRuleTarget.AllCards, M(2026, 11), 6500m, revision: 1);
        var restore = Exception(BudgetRuleTarget.AllCards, M(2026, 11), null, revision: 2, action: BudgetRuleAction.Restore);
        var standard = Standard(BudgetRuleTarget.AllCards, M(2026, 11), 5000m);
        Assert.Equal(TargetOrigin.Inherited, Card(Layers([standard, set, restore]), M(2026, 11)).Origin);
        Assert.Equal(5000m, Card(Layers([standard, set, restore]), M(2026, 11)).Amount);
        var again = Exception(BudgetRuleTarget.AllCards, M(2026, 11), 7000m, revision: 3);
        Assert.Equal(7000m, Card(Layers([standard, set, restore, again]), M(2026, 11)).Amount);
    }

    [Fact]
    public void InheritedAndExceptionAreNeverAddedUpEachKeyResolvesToExactlyOneValue()
    {
        var layers = Layers([Standard(BudgetRuleTarget.AllCards, M(2026, 11), 5000m), Exception(BudgetRuleTarget.AllCards, M(2026, 11), 6500m),
            Standard(BudgetRuleTarget.Category, M(2026, 11), 800m, category: Market), Exception(BudgetRuleTarget.Category, M(2026, 11), 900m, category: Market)]);
        var resolved = EffectiveBudgetResolver.Resolve(Workspace, layers, M(2026, 11));
        Assert.Single(resolved.Targets, t => t.Target == BudgetRuleTarget.AllCards);
        Assert.Single(resolved.Targets, t => t.Target == BudgetRuleTarget.Category && t.CategoryId == Market);
        Assert.Equal(6500m, resolved.CardTarget); Assert.Equal(900m, resolved.Find(BudgetRuleTarget.Category, Market)!.Amount);
    }

    [Fact]
    public void CardsAndCategoriesAreComplementaryAndTheTotalIsItsOwnTargetNeverTheirSum()
    {
        var layers = Layers([Standard(BudgetRuleTarget.TotalExpenses, M(2026, 11), 12500m), Standard(BudgetRuleTarget.AllCards, M(2026, 11), 5000m), Standard(BudgetRuleTarget.FixedExpenses, M(2026, 11), 3000m),
            Standard(BudgetRuleTarget.Category, M(2026, 11), 800m, category: Market)]);
        var resolved = EffectiveBudgetResolver.Resolve(Workspace, layers, M(2026, 11));
        Assert.Equal(12500m, resolved.TotalExpenses);
        Assert.Equal(3000m, resolved.FixedTarget); // despesas fixas NÃO são sinônimo de "fora dos cartões" (7.500)
        Assert.Equal(12500m - 5000m, resolved.TotalExpenses - resolved.CardTarget);
        Assert.Empty(EffectiveBudgetResolver.Validate(resolved, layers.CategoryParents));
    }

    [Fact]
    public void AnArchivedHistoricalTargetCountsAsRemovedAndBlocksInheritanceAndAnExceptionBeatsHistory()
    {
        var legacyTarget = Guid.NewGuid();
        var november = new LegacyMonthFact(M(2026, 11), Guid.NewGuid(), [new(BudgetRuleTarget.AllCards, null, 3000m, Archived: true, legacyTarget)], null);
        var layers = Layers([Standard(BudgetRuleTarget.AllCards, M(2026, 10), 5000m)], [november]);
        var removed = Card(layers, M(2026, 11));
        Assert.True(removed.Removed); Assert.Null(removed.Amount); Assert.Equal(TargetOrigin.Historical, removed.Origin); Assert.Equal(legacyTarget, removed.LegacyTargetId);
        Assert.Equal(5000m, Card(layers, M(2026, 12)).Amount);
        var withException = Layers([Standard(BudgetRuleTarget.AllCards, M(2026, 10), 5000m), Exception(BudgetRuleTarget.AllCards, M(2026, 11), 4100m)], [november]);
        Assert.Equal(4100m, Card(withException, M(2026, 11)).Amount);
    }

    [Fact]
    public void AHistoricalMonthWithOnlySomeTargetsStillInheritsTheOthers()
    {
        var november = new LegacyMonthFact(M(2026, 11), Guid.NewGuid(), [new(BudgetRuleTarget.FixedExpenses, null, 3300m, false, Guid.NewGuid())], null);
        var layers = Layers([Standard(BudgetRuleTarget.AllCards, M(2026, 11), 5000m), Standard(BudgetRuleTarget.FixedExpenses, M(2026, 11), 3000m)], [november]);
        var resolved = EffectiveBudgetResolver.Resolve(Workspace, layers, M(2026, 11));
        Assert.Equal(5000m, resolved.CardTarget); Assert.Equal(TargetOrigin.Inherited, resolved.Find(BudgetRuleTarget.AllCards)!.Origin);
        Assert.Equal(3300m, resolved.FixedTarget); Assert.Equal(TargetOrigin.Historical, resolved.Find(BudgetRuleTarget.FixedExpenses)!.Origin);
    }

    // ---- Validação, identificação e pontos de mudança ---------------------------------------------------------------------------------------------------

    [Fact]
    public void ATotalSmallerThanTheCardsTargetIsRejectedAndOverlappingCategoriesToo()
    {
        var layers = Layers([Standard(BudgetRuleTarget.TotalExpenses, M(2026, 11), 4000m), Standard(BudgetRuleTarget.AllCards, M(2026, 11), 5000m),
            Standard(BudgetRuleTarget.Category, M(2026, 11), 800m, category: Food), Standard(BudgetRuleTarget.Category, M(2026, 11), 300m, category: Groceries)],
            parents: new Dictionary<Guid, Guid?> { [Food] = null, [Groceries] = Food });
        var errors = EffectiveBudgetResolver.Validate(EffectiveBudgetResolver.Resolve(Workspace, layers, M(2026, 11)), layers.CategoryParents);
        Assert.Contains(EffectiveBudgetResolver.TotalBelowCardsMessage, errors); Assert.Contains(EffectiveBudgetResolver.OverlappingCategoriesMessage, errors);
    }

    [Fact]
    public void ATotalEqualToOrAboveTheCardsOrAnUndefinedSideIsValid()
    {
        var equal = Layers([Standard(BudgetRuleTarget.TotalExpenses, M(2026, 11), 5000m), Standard(BudgetRuleTarget.AllCards, M(2026, 11), 5000m)]);
        Assert.Empty(EffectiveBudgetResolver.Validate(EffectiveBudgetResolver.Resolve(Workspace, equal, M(2026, 11)), equal.CategoryParents));
        var onlyCards = Layers([Standard(BudgetRuleTarget.AllCards, M(2026, 11), 5000m)]);
        Assert.Empty(EffectiveBudgetResolver.Validate(EffectiveBudgetResolver.Resolve(Workspace, onlyCards, M(2026, 11)), onlyCards.CategoryParents));
    }

    [Fact]
    public void TheStableIdIsTheSameInEveryMonthAndDiffersByKeyAndWorkspace()
    {
        var layers = Layers([Standard(BudgetRuleTarget.AllCards, M(2026, 11), 5000m)]);
        Assert.Equal(Card(layers, M(2026, 11)).Id, Card(layers, M(2027, 3)).Id);
        Assert.NotEqual(EffectiveBudgetResolver.StableId(Workspace, BudgetRuleTarget.AllCards, null), EffectiveBudgetResolver.StableId(Workspace, BudgetRuleTarget.FixedExpenses, null));
        Assert.NotEqual(EffectiveBudgetResolver.StableId(Workspace, BudgetRuleTarget.Category, Food), EffectiveBudgetResolver.StableId(Workspace, BudgetRuleTarget.Category, Market));
        Assert.NotEqual(EffectiveBudgetResolver.StableId(Workspace, BudgetRuleTarget.AllCards, null), EffectiveBudgetResolver.StableId(Guid.NewGuid(), BudgetRuleTarget.AllCards, null));
    }

    [Fact]
    public void ChangePointsListTheMonthAndEveryLaterVigenciaOrException()
    {
        var layers = Layers([Standard(BudgetRuleTarget.AllCards, M(2026, 11), 5000m), Exception(BudgetRuleTarget.AllCards, M(2027, 2), 100m), Standard(BudgetRuleTarget.AllCards, M(2027, 5), 5100m)]);
        Assert.Equal([M(2026, 12), M(2027, 2), M(2027, 5)], EffectiveBudgetResolver.ChangePoints(layers, M(2026, 12)));
    }
}
