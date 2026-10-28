using Saldo360.Domain.Common;

namespace Saldo360.Domain.Pluggy;

public enum PluggyLinkedAccountKind { Bank, Card }

/// <summary>Situação da integração da conta. Derivada da última sincronização; nunca bloqueia o uso manual da conta.</summary>
public enum PluggyAccountLinkStatus { Active, ConsentExpired, Attention }

/// <summary>Valores informados pelo banco na última leitura. Ficam separados do saldo calculado pelo Saldo360.</summary>
public sealed record PluggyObservedFigures(decimal? Balance, DateTimeOffset? ObservedAt, decimal? OverdraftContractedLimit,
    decimal? OverdraftUsedLimit, decimal? CreditLimit, decimal? AvailableCreditLimit);

/// <summary>
/// Vínculo persistente entre uma conta do Saldo360 (conta bancária ou cartão) e uma conta externa da Pluggy, dentro de um Item já
/// cadastrado no orçamento. É a única ponte entre o formato da Pluggy e o modelo financeiro: o núcleo não conhece identificadores
/// externos. Não guarda número de conta, CPF/CNPJ nem credenciais; só o rótulo mascarado.
/// </summary>
public sealed class PluggyAccountLink : Entity
{
    public const int NameMaxLength = 160;
    private PluggyAccountLink() { PluggyAccountId = string.Empty; DisplayName = string.Empty; }

    public Guid WorkspaceId { get; private init; }
    public Guid ConnectionId { get; private init; }
    public string PluggyAccountId { get; private init; }
    public PluggyLinkedAccountKind Kind { get; private init; }
    /// <summary>Conta bancária do Saldo360 (somente <see cref="PluggyLinkedAccountKind.Bank"/>).</summary>
    public Guid? FinancialAccountId { get; private init; }
    /// <summary>Cartão do Saldo360 (somente <see cref="PluggyLinkedAccountKind.Card"/>).</summary>
    public Guid? CreditCardId { get; private init; }
    /// <summary>Instituição/nome exibido, ex.: "Itaú · Conta corrente ••••6789".</summary>
    public string DisplayName { get; private set; }
    public string? InstitutionName { get; private set; }
    public string? PluggyType { get; private set; }
    public string? PluggySubtype { get; private set; }
    public PluggyAccountLinkStatus Status { get; private set; }
    /// <summary>Movimentações anteriores a esta data não alimentam o realizado (evita sobrepor histórico já lançado manualmente).</summary>
    public DateOnly? ImportFrom { get; private init; }
    public DateTimeOffset LinkedAt { get; private init; }
    public Guid LinkedByUserId { get; private init; }
    public DateTimeOffset? LastSyncedAt { get; private set; }
    public decimal? ObservedBalance { get; private set; }
    public DateTimeOffset? ObservedAt { get; private set; }
    public decimal? OverdraftContractedLimit { get; private set; }
    public decimal? OverdraftUsedLimit { get; private set; }
    public decimal? CreditLimit { get; private set; }
    public decimal? AvailableCreditLimit { get; private set; }

    public static PluggyAccountLink Create(Guid workspaceId, Guid connectionId, string pluggyAccountId, PluggyLinkedAccountKind kind,
        Guid? financialAccountId, Guid? creditCardId, string displayName, string? institutionName, string? pluggyType, string? pluggySubtype,
        DateOnly? importFrom, Guid userId, DateTimeOffset now)
    {
        if (workspaceId == Guid.Empty || connectionId == Guid.Empty || userId == Guid.Empty) throw new ArgumentException("Orçamento, conexão ou usuário inválido.");
        if (string.IsNullOrWhiteSpace(pluggyAccountId) || pluggyAccountId.Length > 64) throw new ArgumentException("Identificador da conta externa inválido.");
        if ((kind == PluggyLinkedAccountKind.Bank) != (financialAccountId is not null) || (kind == PluggyLinkedAccountKind.Card) != (creditCardId is not null))
            throw new ArgumentException("O vínculo deve apontar para uma conta bancária ou para um cartão, conforme o tipo.");
        displayName = displayName?.Trim() ?? string.Empty;
        if (displayName.Length == 0) throw new ArgumentException("Nome exibido obrigatório.");
        return new()
        {
            WorkspaceId = workspaceId, ConnectionId = connectionId, PluggyAccountId = pluggyAccountId.Trim(), Kind = kind,
            FinancialAccountId = financialAccountId, CreditCardId = creditCardId, DisplayName = Clip(displayName, NameMaxLength)!,
            InstitutionName = Clip(institutionName, 120), PluggyType = Clip(pluggyType, 32), PluggySubtype = Clip(pluggySubtype, 40),
            Status = PluggyAccountLinkStatus.Active, ImportFrom = importFrom, LinkedAt = now, LinkedByUserId = userId,
        };
    }

    public void Observe(PluggyObservedFigures figures)
    {
        ObservedBalance = figures.Balance;
        ObservedAt = figures.ObservedAt;
        OverdraftContractedLimit = figures.OverdraftContractedLimit;
        OverdraftUsedLimit = figures.OverdraftUsedLimit;
        CreditLimit = figures.CreditLimit;
        AvailableCreditLimit = figures.AvailableCreditLimit;
    }

    public void MarkSynced(DateTimeOffset now) { LastSyncedAt = now; Status = PluggyAccountLinkStatus.Active; }

    public void SetStatus(PluggyAccountLinkStatus status) => Status = status;

    public void Rename(string displayName) => DisplayName = Clip(displayName.Trim(), NameMaxLength) ?? DisplayName;

    private static string? Clip(string? value, int max) => string.IsNullOrWhiteSpace(value) ? null : value.Trim().Length > max ? value.Trim()[..max] : value.Trim();
}
