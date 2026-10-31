using Saldo360.Domain.Categories;

namespace Saldo360.Application.Categories;

/// <summary>O que acontece com um item do catálogo num orçamento.</summary>
public enum DefaultCategoryAction
{
    /// <summary>Não existe: será criado.</summary>
    Create,

    /// <summary>Já existe (mesmo tipo/nível e nome equivalente): é reutilizado, sem alteração.</summary>
    Exists,

    /// <summary>Já existe, mas inativo: é reutilizado e continua inativo (nada é reativado silenciosamente).</summary>
    ExistsInactive,

    /// <summary>Subcategoria ausente sob uma categoria principal inativa: não é criada, para não gerar uma subcategoria já indisponível.</summary>
    SkippedParentInactive,
}

public sealed record DefaultSubcategoryPlan(string Name, DefaultCategoryAction Action, Guid? ExistingId);

public sealed record DefaultCategoryPlan(
    string Name, CategoryType Type, DefaultCategoryAction Action, Guid? ExistingId, IReadOnlyList<DefaultSubcategoryPlan> Subcategories);

/// <summary>Resultado de comparar o catálogo com as categorias de um orçamento; serve à pré-visualização, à ação explícita e à inicialização.</summary>
public sealed record DefaultCategoriesPlan(int CatalogVersion, IReadOnlyList<DefaultCategoryPlan> Categories)
{
    public int CategoriesToCreate => Categories.Count(c => c.Action == DefaultCategoryAction.Create);

    public int SubcategoriesToCreate => Categories.Sum(c => c.Subcategories.Count(s => s.Action == DefaultCategoryAction.Create));

    public bool HasChanges => CategoriesToCreate + SubcategoriesToCreate > 0;

    /// <summary>
    /// Entidades novas, prontas para gravar. <paramref name="existing"/> traz as categorias já gravadas do orçamento, para ligar subcategorias novas
    /// a principais reutilizadas.
    /// </summary>
    public IReadOnlyList<FinancialCategory> CreateEntities(
        Guid workspaceId, Guid userId, DateTimeOffset now, IReadOnlyDictionary<Guid, FinancialCategory> existing)
    {
        var created = new List<FinancialCategory>();
        foreach (var category in Categories)
        {
            var main = category.Action == DefaultCategoryAction.Create
                ? FinancialCategory.Create(workspaceId, category.Name, category.Type, userId, now)
                : null;
            if (main is not null)
            {
                created.Add(main);
            }

            var parent = main ?? (category.ExistingId is { } id ? existing[id] : null);
            foreach (var sub in category.Subcategories.Where(s => s.Action == DefaultCategoryAction.Create))
            {
                created.Add(FinancialCategory.CreateSubcategory(parent!, sub.Name, userId, now));
            }
        }

        return created;
    }
}

/// <summary>
/// Compara o catálogo com as categorias existentes pelo tipo e pelo nome normalizado (espaços e caixa; nomes apenas parecidos nunca são unidos).
/// Subcategorias são comparadas dentro da mesma categoria principal. Não altera nem reativa nada: só decide o que criar.
/// </summary>
public static class DefaultCategoryPlanner
{
    public static DefaultCategoriesPlan Plan(IDefaultCategoryCatalog catalog, IReadOnlyCollection<FinancialCategoryDto> existing)
    {
        var mains = existing.Where(c => c.ParentCategoryId is null).ToList();
        var subsByParent = existing.Where(c => c.ParentCategoryId is not null).ToLookup(c => c.ParentCategoryId!.Value);

        var plans = new List<DefaultCategoryPlan>(catalog.Categories.Count);
        foreach (var definition in catalog.Categories)
        {
            var main = Find(mains.Where(c => c.Type == definition.Type), definition.Name);
            var subs = definition.Subcategories.Select(name => PlanSubcategory(name, main, subsByParent)).ToList();
            plans.Add(new DefaultCategoryPlan(definition.Name, definition.Type, ActionOf(main), main?.Id, subs));
        }

        return new DefaultCategoriesPlan(catalog.Version, plans);
    }

    private static DefaultSubcategoryPlan PlanSubcategory(string name, FinancialCategoryDto? main, ILookup<Guid, FinancialCategoryDto> subsByParent)
    {
        if (main is null)
        {
            return new DefaultSubcategoryPlan(name, DefaultCategoryAction.Create, null);
        }

        var sub = Find(subsByParent[main.Id], name);
        if (sub is not null)
        {
            return new DefaultSubcategoryPlan(name, ActionOf(sub), sub.Id);
        }

        return new DefaultSubcategoryPlan(name, main.IsActive ? DefaultCategoryAction.Create : DefaultCategoryAction.SkippedParentInactive, null);
    }

    /// <summary>Em caso de equivalentes (nomes que só diferem em espaços), prefere a ativa.</summary>
    private static FinancialCategoryDto? Find(IEnumerable<FinancialCategoryDto> candidates, string name)
    {
        var key = FinancialCategory.MatchKey(name);
        return candidates.Where(c => FinancialCategory.MatchKey(c.Name) == key).OrderByDescending(c => c.IsActive).FirstOrDefault();
    }

    private static DefaultCategoryAction ActionOf(FinancialCategoryDto? existing) => existing switch
    {
        null => DefaultCategoryAction.Create,
        { IsActive: true } => DefaultCategoryAction.Exists,
        _ => DefaultCategoryAction.ExistsInactive,
    };
}
