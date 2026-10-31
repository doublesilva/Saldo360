using Saldo360.Domain.Pluggy;

namespace Saldo360.Application.Pluggy;

/// <summary>Uma ponta candidata a transferência, já com a decisão normalizada do mapper. Não depende de banco nem de rede.</summary>
public sealed record TransferSide(Guid PostingId, Guid LinkId, Guid? FinancialAccountId, PluggyLinkedAccountKind LinkKind, PluggyDisposition Disposition, bool IsDebit,
    decimal Amount, DateOnly Date, string Operation, PluggyPostingState State, PluggyPostingKind Kind, Guid? CounterpartPostingId, PluggyResolution? Resolution);

/// <summary>
/// Regras únicas de elegibilidade de duas pontas para formar uma transferência entre contas do mesmo orçamento. Servem à confirmação manual (que valida
/// de novo dentro da transação) e à lista de possíveis contrapartes da revisão. A inferência automática usa o mesmo critério por meio de
/// <see cref="PluggyPairing"/> (valor, sentidos, mesmo dia, operação, contas distintas, unicidade); aqui ficam as demais condições de uma ponta.
/// Valor e data coincidentes são inferência, nunca prova de titularidade: a descrição não participa.
/// </summary>
public static class PluggyTransferEligibility
{
    /// <summary>Versão da regra de pareamento gravada nos vínculos inferidos.</summary>
    public const string RuleVersion = "transfer-v2";

    public static class Reasons
    {
        public const string SameDirection = "SameDirection";
        public const string SameAccount = "SameAccount";
        public const string NotBankAccount = "NotBankAccount";
        public const string NotTransferEvidence = "NotTransferEvidence";
        public const string ManualDecision = "ManualDecision";
        public const string DifferentAmount = "DifferentAmount";
        public const string DifferentDate = "DifferentDate";
        public const string IncompatibleOperation = "IncompatibleOperation";
        public const string EndUnavailable = "EndUnavailable";
        public const string EndUsedElsewhere = "EndUsedElsewhere";
    }

    /// <summary>Operações bancárias que podem ser pontas de uma transferência entre contas do usuário.</summary>
    public static bool IsTransferOperation(string normalizedOperation) =>
        normalizedOperation is "PIX" or "TED" or "DOC" or "TRANSFERENCIA_MESMA_INSTITUICAO";

    /// <summary>Operação normalizada de uma transação importada (consistente entre mapper, motor e confirmação).</summary>
    public static string OperationOf(string? operationType) => PluggyOperationCodes.Normalize(operationType);

    /// <returns>Null se as duas pontas podem formar a transferência; senão o código estável do primeiro impedimento.</returns>
    public static string? Check(TransferSide a, TransferSide b)
    {
        if (a.IsDebit == b.IsDebit) return Reasons.SameDirection;
        if (a.LinkKind != PluggyLinkedAccountKind.Bank || b.LinkKind != PluggyLinkedAccountKind.Bank) return Reasons.NotBankAccount;
        if (a.LinkId == b.LinkId || a.FinancialAccountId is null || b.FinancialAccountId is null || a.FinancialAccountId == b.FinancialAccountId) return Reasons.SameAccount;
        if (End(a) is { } problemA) return problemA;
        if (End(b) is { } problemB) return problemB;
        if (a.Amount != b.Amount) return Reasons.DifferentAmount;
        if (a.Date != b.Date) return Reasons.DifferentDate;
        if (a.Operation.Length == 0 || a.Operation != b.Operation || !IsTransferOperation(a.Operation)) return Reasons.IncompatibleOperation;
        return null;
    }

    private static string? End(TransferSide side)
    {
        // Uma decisão manual (receita, despesa ou ignorar) diz que a ponta não é transferência: só é revertida pelo usuário, nunca pela inferência.
        if (side.Resolution is not null) return Reasons.ManualDecision;
        if (side.Disposition is not (PluggyDisposition.TransferProbe or PluggyDisposition.TransferCandidate)) return Reasons.NotTransferEvidence;
        return side.State switch
        {
            PluggyPostingState.NeedsReview => null,
            PluggyPostingState.Applied when side.Kind == PluggyPostingKind.BankMovement => null,
            PluggyPostingState.Applied when side.Kind == PluggyPostingKind.Transfer => null, // o chamador decide se é o mesmo par (idempotência) ou outro vínculo
            PluggyPostingState.Applied or PluggyPostingState.Covered => Reasons.EndUsedElsewhere, // pagamento de fatura, compra de cartão...
            _ => Reasons.EndUnavailable,
        };
    }

    /// <summary>Verdadeiro se as duas pontas já são o par de uma transferência aplicada (confirmar de novo não muda efeito algum).</summary>
    public static bool AlreadyLinkedToEachOther(TransferSide a, TransferSide b) =>
        a.Kind == PluggyPostingKind.Transfer && b.Kind == PluggyPostingKind.Transfer && a.State == PluggyPostingState.Applied && b.State == PluggyPostingState.Applied
        && a.CounterpartPostingId == b.PostingId && b.CounterpartPostingId == a.PostingId;

    /// <summary>Verdadeiro se alguma ponta já pertence a uma transferência aplicada com outra contraparte.</summary>
    public static bool UsedByAnotherLink(TransferSide a, TransferSide b) =>
        (a.Kind == PluggyPostingKind.Transfer && a.State == PluggyPostingState.Applied && a.CounterpartPostingId != b.PostingId)
        || (b.Kind == PluggyPostingKind.Transfer && b.State == PluggyPostingState.Applied && b.CounterpartPostingId != a.PostingId);
}
