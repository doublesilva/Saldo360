using Saldo360.Domain.Common;

namespace Saldo360.Domain.Accounts;

/// <summary>
/// Natureza do saldo informado. Saldo contábil/atual, saldo disponível e limite de crédito são coisas diferentes: o limite do cheque especial
/// nunca é somado ao saldo, e um saldo de natureza desconhecida não serve de referência confiável.
/// </summary>
public enum BalanceAnchorKind { CurrentBalance, AvailableBalance, Unknown }

public enum BalanceAnchorReliability
{
    /// <summary>Coleta completa, conexão ativa, natureza do saldo conhecida e nenhum movimento em aberto no histórico usado.</summary>
    Reliable,
    /// <summary>Utilizável, mas só a partir da data derivada: há movimentos pendentes ou em revisão mais antigos que limitam o histórico calculável.</summary>
    Provisional,
    /// <summary>Não permite cálculo confiável (coleta incompleta, consentimento vencido, natureza do saldo incerta ou saldo incoerente).</summary>
    Unreliable,
}

/// <summary>
/// Referência bancária de saldo de uma conta integrada (T09.3.2): valor observado, instante e data de negócio, natureza, origem e confiabilidade.
/// Serve de base para calcular o saldo sem exigir cadastro manual de saldo inicial, mas NÃO é lançamento: não gera receita nem despesa, não é
/// ajuste e nunca é aplicada sozinha sobre o cadastro da conta. O saldo inicial derivado (valor e data) existe só para o cálculo e é recalculado a
/// cada nova observação; o saldo inicial manual, quando existe, prevalece.
/// </summary>
public sealed class FinancialAccountBalanceAnchor : Entity
{
    public const int SourceMaxLength = 16;
    public const int ReasonMaxLength = 48;
    private FinancialAccountBalanceAnchor() { Source = string.Empty; }

    public Guid WorkspaceId { get; private init; }
    public Guid AccountId { get; private init; }
    public decimal Amount { get; private init; }
    public DateTimeOffset ObservedAt { get; private init; }
    /// <summary>Data de negócio da observação (dia UTC): movimentos até esta data já estão refletidos no valor.</summary>
    public DateOnly AsOfDate { get; private init; }
    public BalanceAnchorKind Kind { get; private init; }
    public string Source { get; private init; }
    public Guid? SourceLinkId { get; private init; }
    public BalanceAnchorReliability Reliability { get; private set; }
    public string? UnavailableReason { get; private set; }
    /// <summary>Saldo no início de <see cref="DerivedOpeningDate"/> que, somado aos movimentos até <see cref="AsOfDate"/>, reproduz o valor observado.</summary>
    public decimal? DerivedOpeningAmount { get; private set; }
    public DateOnly? DerivedOpeningDate { get; private set; }
    public bool IsCurrent { get; private set; }
    public DateTimeOffset CreatedAt { get; private init; }

    public bool IsUsable => IsCurrent && Reliability != BalanceAnchorReliability.Unreliable && DerivedOpeningAmount is not null && DerivedOpeningDate is not null;

    public static FinancialAccountBalanceAnchor Create(Guid workspaceId, Guid accountId, decimal amount, DateTimeOffset observedAt, DateOnly asOfDate, BalanceAnchorKind kind,
        string source, Guid? sourceLinkId, DateTimeOffset now)
    {
        if (workspaceId == Guid.Empty || accountId == Guid.Empty || string.IsNullOrWhiteSpace(source) || source.Length > SourceMaxLength) throw new ArgumentException("Âncora de saldo inválida.");
        return new() { WorkspaceId = workspaceId, AccountId = accountId, Amount = amount, ObservedAt = observedAt, AsOfDate = asOfDate, Kind = kind, Source = source, SourceLinkId = sourceLinkId, IsCurrent = true, CreatedAt = now };
    }

    /// <summary>Uma observação mais nova substituiu esta (o histórico é mantido).</summary>
    public void Supersede() => IsCurrent = false;

    public void Derive(BalanceAnchorReliability reliability, string? reason, decimal? openingAmount, DateOnly? openingDate)
    {
        Reliability = reliability;
        UnavailableReason = reason is { Length: > ReasonMaxLength } ? reason[..ReasonMaxLength] : reason;
        var usable = reliability != BalanceAnchorReliability.Unreliable && openingAmount is not null && openingDate is not null;
        DerivedOpeningAmount = usable ? openingAmount : null; DerivedOpeningDate = usable ? openingDate : null;
    }
}
