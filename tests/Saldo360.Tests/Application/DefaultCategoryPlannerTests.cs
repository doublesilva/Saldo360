using Saldo360.Application.Categories;
using Saldo360.Domain.Categories;

namespace Saldo360.Tests.Application;

// Contratos puros do catálogo padrão e da comparação com categorias existentes: sem API, banco ou fixture.

[Trait("Category", "CORE")]
[Trait("Area", "Categories")]
[Trait("Profile", "FAST")]
public sealed class DefaultCategoryPlannerTests
{
    private static readonly DateTimeOffset Now = DateTimeOffset.Parse("2026-10-31T12:00:00Z");
    private static readonly Guid Workspace = Guid.CreateVersion7();
    private static readonly Guid User = Guid.CreateVersion7();

    [Fact]
    public void StandardCatalog_HasTenExpenseAndFourIncomeCategories_WithTheAgreedSubcategories()
    {
        var catalog = StandardDefaultCategoryCatalog.Instance;

        Assert.Equal(10, catalog.Categories.Count(c => c.Type == CategoryType.Expense));
        Assert.Equal(4, catalog.Categories.Count(c => c.Type == CategoryType.Income));
        Assert.Equal(28, catalog.Categories.Where(c => c.Type == CategoryType.Expense).Sum(c => c.Subcategories.Count));
        Assert.Equal(7, catalog.Categories.Where(c => c.Type == CategoryType.Income).Sum(c => c.Subcategories.Count));
        Assert.Equal(
            ["Alimentação", "Moradia", "Transporte", "Saúde e bem-estar", "Educação", "Lazer e compras pessoais", "Tecnologia e comunicação",
             "Impostos e seguros", "Serviços profissionais", "Encargos financeiros"],
            catalog.Categories.Where(c => c.Type == CategoryType.Expense).Select(c => c.Name));
        Assert.Equal(["Salários e benefícios", "Prestação de serviços", "Rendimentos", "Outras receitas"],
            catalog.Categories.Where(c => c.Type == CategoryType.Income).Select(c => c.Name));
        Assert.Equal(["Impostos PF", "Impostos PJ", "Seguros"], catalog.Categories.Single(c => c.Name == "Impostos e seguros").Subcategories);
    }

    [Fact]
    public void StandardCatalog_HasNoDuplicatesNorCategoriesForBanksCardsPeopleOrScopes()
    {
        var catalog = StandardDefaultCategoryCatalog.Instance;

        Assert.Equal(catalog.Categories.Count, catalog.Categories.Select(c => (c.Type, FinancialCategory.MatchKey(c.Name))).Distinct().Count());
        Assert.All(catalog.Categories, c =>
            Assert.Equal(c.Subcategories.Count, c.Subcategories.Select(FinancialCategory.MatchKey).Distinct().Count()));
        Assert.DoesNotContain(catalog.Categories.Select(c => c.Name), name => name is "Sem categoria" or "PF" or "PJ" or "Cartão" or "Banco");
        Assert.All(catalog.Categories.SelectMany(c => c.Subcategories.Prepend(c.Name)), name =>
            Assert.True(FinancialCategory.TryNormalizeName(name, out _, out _)));
    }

    [Fact]
    public void EmptyBudget_PlansEverything_AndBuildsLinkedActiveEntitiesOfTheRightType()
    {
        var plan = DefaultCategoryPlanner.Plan(StandardDefaultCategoryCatalog.Instance, []);
        var entities = plan.CreateEntities(Workspace, User, Now, new Dictionary<Guid, FinancialCategory>());

        Assert.Equal((14, 35), (plan.CategoriesToCreate, plan.SubcategoriesToCreate));
        Assert.Equal(49, entities.Count);
        Assert.All(entities, e => Assert.Equal((Workspace, User, true), (e.WorkspaceId, e.CreatedByUserId, e.IsActive)));
        var mains = entities.Where(e => !e.IsSubcategory).ToDictionary(e => e.Id);
        Assert.All(entities.Where(e => e.IsSubcategory), sub => Assert.Equal(mains[sub.ParentCategoryId!.Value].Type, sub.Type));
        Assert.Equal(entities.Count, entities.Select(e => e.Id).Distinct().Count());
    }

    [Fact]
    public void AfterApplying_PlanningAgainHasNothingToCreate()
    {
        var entities = DefaultCategoryPlanner.Plan(StandardDefaultCategoryCatalog.Instance, [])
            .CreateEntities(Workspace, User, Now, new Dictionary<Guid, FinancialCategory>());

        var again = DefaultCategoryPlanner.Plan(StandardDefaultCategoryCatalog.Instance, ToDtos(entities));

        Assert.False(again.HasChanges);
        Assert.All(again.Categories, c => Assert.Equal(DefaultCategoryAction.Exists, c.Action));
        Assert.All(again.Categories.SelectMany(c => c.Subcategories), s => Assert.Equal(DefaultCategoryAction.Exists, s.Action));
    }

    [Fact]
    public void EquivalentNames_AreReused_IgnoringCaseAndSpaces_ButNotAccentsOrSimilarNames()
    {
        var catalog = new TestCatalog(
            new DefaultCategoryDefinition("Alimentação", CategoryType.Expense, ["Mercado e feira"]),
            new DefaultCategoryDefinition("Moradia", CategoryType.Expense, ["Contas da casa"]),
            new DefaultCategoryDefinition("Salários e benefícios", CategoryType.Income, ["Salário"]));
        var food = Dto("  ALIMENTAÇÃO ", CategoryType.Expense);
        var existing = new[]
        {
            food,
            Dto("mercado   E  feira", CategoryType.Expense, food.Id),
            Dto("Moradía", CategoryType.Expense),            // acento diferente: não é a mesma
            Dto("Salários e benefícios", CategoryType.Expense), // tipo diferente: não é a mesma
        };

        var plan = DefaultCategoryPlanner.Plan(catalog, existing);

        var alimentacao = plan.Categories[0];
        Assert.Equal((DefaultCategoryAction.Exists, food.Id), (alimentacao.Action, alimentacao.ExistingId));
        Assert.Equal(DefaultCategoryAction.Exists, Assert.Single(alimentacao.Subcategories).Action);
        Assert.Equal(DefaultCategoryAction.Create, plan.Categories[1].Action);
        Assert.Equal(DefaultCategoryAction.Create, plan.Categories[2].Action);
        Assert.Equal(DefaultCategoryAction.Create, Assert.Single(plan.Categories[2].Subcategories).Action);
    }

    [Fact]
    public void Subcategories_AreComparedOnlyInsideTheSameMainCategory()
    {
        var catalog = new TestCatalog(new DefaultCategoryDefinition("Moradia", CategoryType.Expense, ["Contas da casa"]));
        var other = Dto("Trabalho", CategoryType.Expense);
        var housing = Dto("Moradia", CategoryType.Expense);
        var existing = new[] { other, housing, Dto("Contas da casa", CategoryType.Expense, other.Id) };

        var plan = DefaultCategoryPlanner.Plan(catalog, existing);

        var sub = Assert.Single(Assert.Single(plan.Categories).Subcategories);
        Assert.Equal(DefaultCategoryAction.Create, sub.Action);
        Assert.Null(sub.ExistingId);
        var created = plan.CreateEntities(Workspace, User, Now, ToEntities(existing));
        var newSub = Assert.Single(created);
        Assert.Equal(housing.Id, newSub.ParentCategoryId);
    }

    [Fact]
    public void InactiveCategories_AreReusedButNeverReactivated_AndTheirMissingSubcategoriesAreSkipped()
    {
        var catalog = new TestCatalog(new DefaultCategoryDefinition("Moradia", CategoryType.Expense, ["Contas da casa", "Aluguel e financiamento"]));
        var housing = Dto("Moradia", CategoryType.Expense, isActive: false);
        var existing = new[] { housing, Dto("Contas da casa", CategoryType.Expense, housing.Id, isActive: false) };

        var plan = DefaultCategoryPlanner.Plan(catalog, existing);

        var category = Assert.Single(plan.Categories);
        Assert.Equal(DefaultCategoryAction.ExistsInactive, category.Action);
        Assert.Equal([DefaultCategoryAction.ExistsInactive, DefaultCategoryAction.SkippedParentInactive], category.Subcategories.Select(s => s.Action));
        Assert.False(plan.HasChanges);
        Assert.Empty(plan.CreateEntities(Workspace, User, Now, ToEntities(existing)));
    }

    [Fact]
    public void ActiveMainWithInactiveSubcategory_KeepsItInactive_AndCreatesTheOthers()
    {
        var catalog = new TestCatalog(new DefaultCategoryDefinition("Moradia", CategoryType.Expense, ["Contas da casa", "Aluguel e financiamento"]));
        var housing = Dto("Moradia", CategoryType.Expense);
        var existing = new[] { housing, Dto("Contas da casa", CategoryType.Expense, housing.Id, isActive: false) };

        var plan = DefaultCategoryPlanner.Plan(catalog, existing);

        Assert.Equal([DefaultCategoryAction.ExistsInactive, DefaultCategoryAction.Create], plan.Categories[0].Subcategories.Select(s => s.Action));
        Assert.Equal((0, 1), (plan.CategoriesToCreate, plan.SubcategoriesToCreate));
    }

    private static FinancialCategoryDto Dto(string name, CategoryType type, Guid? parent = null, bool isActive = true) =>
        new(Guid.CreateVersion7(), name, type, isActive, Now, parent);

    private static List<FinancialCategoryDto> ToDtos(IEnumerable<FinancialCategory> entities) =>
        entities.Select(e => new FinancialCategoryDto(e.Id, e.Name, e.Type, e.IsActive, e.CreatedAt, e.ParentCategoryId)).ToList();

    // Entidades de mentira só para ligar subcategorias novas a principais já existentes.
    private static Dictionary<Guid, FinancialCategory> ToEntities(IEnumerable<FinancialCategoryDto> dtos) =>
        dtos.Where(d => d.ParentCategoryId is null)
            .ToDictionary(d => d.Id, d => ReuseId(FinancialCategory.Create(Workspace, d.Name, d.Type, User, Now), d.Id));

    private static FinancialCategory ReuseId(FinancialCategory category, Guid id)
    {
        typeof(FinancialCategory).BaseType!.GetProperty(nameof(FinancialCategory.Id))!.SetValue(category, id);
        return category;
    }

    private sealed class TestCatalog(params DefaultCategoryDefinition[] categories) : IDefaultCategoryCatalog
    {
        public int Version => 99;

        public IReadOnlyList<DefaultCategoryDefinition> Categories { get; } = categories;
    }
}
