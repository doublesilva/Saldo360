using Saldo360.Domain.Categories;

namespace Saldo360.Infrastructure.Persistence.Configurations;

/// <summary>Constraint comum da natureza da despesa (R02): só valores conhecidos, e receita nunca tem natureza.</summary>
internal static class ExpenseNatureCheck
{
    /// <param name="typeColumn">Coluna do tipo (Income/Expense); nulo quando a tabela só tem despesas.</param>
    public static string Sql(string? typeColumn, string natureColumn)
    {
        var known = $"{natureColumn} IN ({string.Join(", ", ExpenseNatures.Names.Select(name => $"'{name}'"))})";
        return typeColumn is null ? known : $"{known} AND ({typeColumn} = 'Expense' OR {natureColumn} = 'Undefined')";
    }
}
