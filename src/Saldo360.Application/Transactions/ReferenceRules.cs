using Saldo360.Domain.Categories;

namespace Saldo360.Application.Transactions;

/// <summary>Consulta de conta e categoria da família, para validar referências de lançamentos e previsões.</summary>
public interface IReferenceLookup
{
    /// <summary>A conta, se pertencer à família; null caso contrário (inexistente ou de outra família).</summary>
    Task<AccountState?> GetAccountStateAsync(Guid workspaceId, Guid accountId, CancellationToken cancellationToken);

    /// <summary>A categoria, se pertencer à família; null caso contrário.</summary>
    Task<CategoryState?> GetCategoryStateAsync(Guid workspaceId, Guid categoryId, CancellationToken cancellationToken);
}

/// <summary>
/// Regras de conta e categoria comuns a lançamentos realizados e previstos: precisam ser da família (outra família
/// recebe a mesma mensagem de uma inexistente), a categoria precisa ser do tipo informado e novas escolhas precisam
/// estar ativas. Uma edição pode manter a conta ou categoria inativa que o registro já usava.
/// </summary>
public static class ReferenceRules
{
    public const string AccountField = "accountId";
    public const string CategoryField = "categoryId";

    /// <param name="currentAccountId">Conta atual do registro editado (null na criação).</param>
    /// <param name="currentCategoryId">Categoria atual do registro editado (null na criação).</param>
    public static async Task ValidateAsync(
        IReferenceLookup lookup, Guid workspaceId, Guid? accountId, Guid? categoryId, string? type,
        Guid? currentAccountId, Guid? currentCategoryId, Dictionary<string, string[]> errors, CancellationToken cancellationToken)
    {
        if (accountId is { } account && !errors.ContainsKey(AccountField))
        {
            var state = await lookup.GetAccountStateAsync(workspaceId, account, cancellationToken);
            if (state is null)
            {
                errors[AccountField] = ["Conta inválida."];
            }
            else if (!state.IsActive && account != currentAccountId)
            {
                errors[AccountField] = ["A conta está inativa. Escolha uma conta ativa."];
            }
        }

        if (categoryId is { } category && !errors.ContainsKey(CategoryField))
        {
            var state = await lookup.GetCategoryStateAsync(workspaceId, category, cancellationToken);
            if (state is null)
            {
                errors[CategoryField] = ["Categoria inválida."];
            }
            else if (CategoryTypes.TryParse(type, out var parsed) && state.Type != parsed)
            {
                errors[CategoryField] = [parsed == CategoryType.Income
                    ? "A categoria é de despesa; escolha uma categoria de receita."
                    : "A categoria é de receita; escolha uma categoria de despesa."];
            }
            else if (!state.IsActive && category != currentCategoryId)
            {
                errors[CategoryField] = ["A categoria está inativa. Escolha uma categoria ativa."];
            }
        }
    }
}
