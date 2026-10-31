namespace Saldo360.Application.Transfers;

public enum TransferRelation
{
    /// <summary>Origem e destino dentro do conjunto selecionado: neutralizada nos agregados (continua nos extratos individuais).</summary>
    Internal,

    /// <summary>Só o destino está no conjunto: a transferência é uma ENTRADA do conjunto (externa a ele).</summary>
    Inbound,

    /// <summary>Só a origem está no conjunto: a transferência é uma SAÍDA do conjunto.</summary>
    Outbound,

    /// <summary>Nenhuma das pontas está no conjunto: não pertence à perspectiva.</summary>
    Outside,
}

/// <summary>
/// Regra única de agregação de transferências por perspectiva (R: conjunto de contas selecionado). Toda tela ou indicador que precise saber se uma transferência soma como entrada,
/// como saída ou é neutralizada usa este predicado; ninguém recalcula isso no frontend.
/// </summary>
public static class TransferPerspective
{
    public static TransferRelation Classify(Guid sourceAccountId, Guid destinationAccountId, IReadOnlySet<Guid> selected)
    {
        var from = selected.Contains(sourceAccountId);
        var to = selected.Contains(destinationAccountId);
        return from && to ? TransferRelation.Internal : to ? TransferRelation.Inbound : from ? TransferRelation.Outbound : TransferRelation.Outside;
    }
}
