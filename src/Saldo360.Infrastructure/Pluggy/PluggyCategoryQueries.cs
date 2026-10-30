using Saldo360.Application.Pluggy;
using Saldo360.Domain.Categories;
using Saldo360.Infrastructure.Persistence;

namespace Saldo360.Infrastructure.Pluggy;

internal static class PluggyCategoryQueries
{
    /// <summary>Projeta as categorias para o mapeamento/classificação, trazendo a principal de cada subcategoria (nome e estado) para o menu "Principal / Sub".</summary>
    public static IQueryable<MappableCategory> ToMappable(this IQueryable<FinancialCategory> categories, Saldo360DbContext db) =>
        categories.Select(c => new MappableCategory(
            c.Id, c.Name, c.Type, c.IsActive, c.ParentCategoryId,
            db.FinancialCategories.Where(p => p.Id == c.ParentCategoryId).Select(p => p.Name).FirstOrDefault(),
            c.ParentCategoryId == null || db.FinancialCategories.Where(p => p.Id == c.ParentCategoryId).Select(p => p.IsActive).FirstOrDefault()));
}
