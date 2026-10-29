using Saldo360.Domain.Common;

namespace Saldo360.Domain.Budgets;

/// <summary>
/// T10.1: meta mensal de consumo dos cartões pagos por uma conta (a conta pagadora padrão dos cartões). Cada linha vale da competência
/// <see cref="EffectiveFrom"/> em diante, até a próxima linha da mesma conta; alterar a meta insere uma linha nova e nunca reescreve as anteriores,
/// de modo que o planejado original de um mês passado não muda quando a meta é revista. <see cref="Amount"/> nulo encerra a meta a partir da
/// competência (volta a "não definido"). Movimentações nunca alteram o valor.
/// </summary>
public sealed class FinancialCardSpendingTarget : Entity
{
    private FinancialCardSpendingTarget() { }

    public Guid WorkspaceId { get; private init; }
    public Guid PayerAccountId { get; private init; }
    public DateOnly EffectiveFrom { get; private init; }
    public decimal? Amount { get; private init; }
    public DateTimeOffset CreatedAt { get; private init; }
    public Guid CreatedByUserId { get; private init; }

    public static FinancialCardSpendingTarget Create(Guid workspaceId, Guid payerAccountId, DateOnly effectiveFrom, decimal? amount, Guid userId, DateTimeOffset now)
    {
        if (workspaceId == Guid.Empty || payerAccountId == Guid.Empty || userId == Guid.Empty) throw new ArgumentException("Orçamento, conta ou usuário inválido.");
        if (effectiveFrom.Day != 1) throw new ArgumentException("A meta vale a partir do primeiro dia de uma competência.");
        if (amount is { } value && (value < 0m || Money.Validate(value, "Meta") is not null)) throw new ArgumentException("Meta de cartões inválida.");
        return new() { WorkspaceId = workspaceId, PayerAccountId = payerAccountId, EffectiveFrom = effectiveFrom, Amount = amount, CreatedAt = now, CreatedByUserId = userId };
    }
}
