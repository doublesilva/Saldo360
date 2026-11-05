using Saldo360.Domain.Common;

namespace Saldo360.Domain.Planning;

/// <summary>
/// Entrega 2: provisão mensal de alimentação paga no cartão (parâmetro do orçamento familiar). A linha sem competência é o padrão do orçamento (valor e categorias alimentares, por
/// identificador); a linha com competência ajusta só o valor daquele mês. É estimativa: nunca vira lançamento e só complementa o que o cartão ainda não reconheceu.
/// </summary>
public sealed class FinancialFoodProvision : Entity
{
    public const decimal DefaultMonthlyAmount = 2000m;

    private FinancialFoodProvision() { }

    public Guid WorkspaceId { get; private init; }
    /// <summary>Nulo = padrão do orçamento; primeiro dia do mês = ajuste daquela competência.</summary>
    public DateOnly? Competence { get; private init; }
    public decimal MonthlyAmount { get; private set; }
    public Guid[] CategoryIds { get; private set; } = [];
    public DateTimeOffset UpdatedAt { get; private set; }
    public Guid UpdatedByUserId { get; private set; }

    public static FinancialFoodProvision Create(Guid workspaceId, DateOnly? competence, decimal amount, IEnumerable<Guid> categoryIds, Guid userId, DateTimeOffset now)
    {
        if (workspaceId == Guid.Empty) throw new ArgumentException("Orçamento inválido.");
        if (competence is { } c && c.Day != 1) throw new ArgumentException("Competência inválida.");
        var entity = new FinancialFoodProvision { WorkspaceId = workspaceId, Competence = competence };
        entity.Revise(amount, categoryIds, userId, now);
        return entity;
    }

    public void Revise(decimal amount, IEnumerable<Guid> categoryIds, Guid userId, DateTimeOffset now)
    {
        if (userId == Guid.Empty) throw new ArgumentException("Usuário inválido.");
        if (amount < 0m || decimal.Round(amount, 2) != amount) throw new ArgumentException("Valor inválido.");
        MonthlyAmount = amount;
        CategoryIds = Competence is null ? categoryIds.Distinct().Order().ToArray() : [];
        UpdatedAt = now; UpdatedByUserId = userId;
    }
}
