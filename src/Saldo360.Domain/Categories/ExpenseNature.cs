using Saldo360.Domain.Accounts;

namespace Saldo360.Domain.Categories;

/// <summary>
/// Natureza da despesa (R02), independente da categoria, do meio de pagamento, da recorrência e do parcelamento.
/// <para>
/// <see cref="Fixed"/> é um compromisso habitual, mesmo que o valor varie (água e luz podem ser fixas). <see cref="Variable"/> é o gasto eventual.
/// <see cref="Undefined"/> é explícita: o registro ainda não foi classificado e nunca é tratado como variável. Receitas nunca têm natureza (sempre
/// <see cref="Undefined"/>, garantido também por constraint). Persistido pelo nome.
/// </para>
/// </summary>
public enum ExpenseNature
{
    Undefined,
    Fixed,
    Variable,
}

/// <summary>Conversão estrita (só os nomes exatos) e regras comuns da natureza.</summary>
public static class ExpenseNatures
{
    public static IReadOnlyList<string> Names { get; } = Enum.GetNames<ExpenseNature>();

    public static bool TryParse(string? value, out ExpenseNature nature) => StrictEnum.TryParse(value, out nature);

    /// <summary>
    /// Interpreta o valor recebido: ausente = <see cref="ExpenseNature.Undefined"/>. Receita só aceita (ou assume) Undefined; qualquer outro valor é erro,
    /// nunca é descartado em silêncio.
    /// </summary>
    public static bool TryResolve(string? value, CategoryType type, out ExpenseNature nature, out string? error)
    {
        nature = ExpenseNature.Undefined;
        error = null;
        if (value is null) return true;
        if (!TryParse(value, out nature))
        {
            error = "Tipo de despesa inválido. Use Fixed, Variable ou Undefined.";
            return false;
        }

        if (type == CategoryType.Income && nature != ExpenseNature.Undefined)
        {
            nature = ExpenseNature.Undefined;
            error = "Receita não tem tipo de despesa (fixa ou variável).";
            return false;
        }

        return true;
    }
}
