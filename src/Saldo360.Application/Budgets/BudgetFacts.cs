using Saldo360.Domain.Budgets;
using Saldo360.Domain.Categories;

namespace Saldo360.Application.Budgets;

/// <summary>De onde vem um registro que alimenta o orçamento. Cada obrigação financeira entra por UMA origem (nunca compra + fatura + pagamento).</summary>
public enum BudgetFactOrigin
{
    /// <summary>Despesa efetiva (lançamento), exceto pagamento de fatura e pagamento de parcela de dívida (quitações, não consumo).</summary>
    Transaction,
    /// <summary>Previsão pendente (sem realização ativa e sem conciliação com fatura ou parcela de dívida).</summary>
    PlannedTransaction,
    /// <summary>Pagamento efetivo de parcela de dívida (consumo em regime de caixa, na data do pagamento).</summary>
    DebtPayment,
    /// <summary>Saldo ainda não pago de uma parcela de dívida, na data de vencimento.</summary>
    DebtInstallment,
    /// <summary>Parcela de compra no cartão, na competência da fatura (mês de vencimento).</summary>
    CardInstallment,
    /// <summary>Parcela histórica/conhecida do cartão, na competência da fatura ou do vencimento informado.</summary>
    CardKnownInstallment,
    /// <summary>Encargo, crédito ou estorno da fatura (crédito reduz o consumo). Saldo anterior e financiado não entram.</summary>
    CardAdjustment,
    /// <summary>Devolução bancária confirmada: fato NEGATIVO na data do crédito, com a categoria e a natureza da despesa devolvida (que mantém valor e data).</summary>
    BankRefund,
}

/// <summary>Como o registro conta: <c>Realized</c> = despesa já efetiva; <c>CardLaunched</c> = compra/parcela já lançada no cartão (consumida mesmo com a fatura em aberto); <c>Pending</c> = compromisso cadastrado ainda não realizado.</summary>
public enum BudgetFactState { Realized, CardLaunched, Pending }

/// <summary>
/// Um registro que consome (ou, se pendente, ainda consumirá) orçamento numa data/competência. <see cref="Amount"/> é assinado: crédito/estorno de
/// cartão é negativo. A mesma lista alimenta o resumo e a composição de cada meta, então os totais sempre coincidem.
/// </summary>
public sealed record BudgetFact(
    BudgetFactOrigin Origin,
    Guid RecordId,
    DateOnly Date,
    decimal Amount,
    BudgetFactState State,
    Guid? CategoryId,
    ExpenseNature Nature,
    string Description,
    Guid? CardId = null,
    string? CardName = null,
    Guid? AccountId = null,
    string? AccountName = null,
    string? InvoiceCompetence = null,
    /// <summary>False quando a competência do cartão veio só do calendário configurado do cartão (compra importada sem fatura do banco): é uma estimativa.</summary>
    bool CompetenceReliable = true,
    /// <summary>Compra do cartão a que a parcela pertence (somente <see cref="BudgetFactOrigin.CardInstallment"/>): é nela que a classificação é corrigida.</summary>
    Guid? PurchaseId = null,
    /// <summary>Veio da integração bancária (Pluggy): categoria e tipo escolhidos pelo usuário sobrevivem às sincronizações.</summary>
    bool Imported = false,
    /// <summary>Registro relacionado: para <see cref="BudgetFactOrigin.BankRefund"/>, a despesa original devolvida (navegação devolução ↔ despesa).</summary>
    Guid? RelatedRecordId = null)
{
    public bool IsCard => CardId is not null;

    /// <summary>
    /// A classificação (categoria e tipo de despesa) tem onde ser corrigida: lançamento, compra de cartão ou previsão. Encargo/estorno de fatura, parcela histórica
    /// e dívida não têm essa correção: ficam explicitamente "sem tipo" em vez de receberem um valor arbitrário.
    /// </summary>
    public bool IsClassifiable => Origin is BudgetFactOrigin.Transaction or BudgetFactOrigin.PlannedTransaction or BudgetFactOrigin.CardInstallment;
}

public static class BudgetTargetMatcher
{
    /// <summary>
    /// Regra única de cobertura. Categoria: o próprio registro ou, numa meta de categoria principal, o das suas subcategorias. Fixas: natureza Fixa,
    /// em qualquer categoria e meio de pagamento (Indefinida e Variável nunca entram). Cartões: qualquer registro de cartão, de qualquer categoria.
    /// </summary>
    public static bool Covers(BudgetTargetGroupingType grouping, Guid? targetCategoryId, IReadOnlyDictionary<Guid, Guid> parentOf, BudgetFact fact) => grouping switch
    {
        BudgetTargetGroupingType.Category => targetCategoryId is { } target && fact.CategoryId is { } category
            && (category == target || parentOf.TryGetValue(category, out var parent) && parent == target),
        BudgetTargetGroupingType.FixedExpenses => fact.Nature == ExpenseNature.Fixed,
        BudgetTargetGroupingType.AllCards => fact.IsCard,
        _ => false,
    };
}

/// <summary>Sinais de dados incompletos de uma meta: nunca viram "zero confirmado".</summary>
public sealed record BudgetCoverage(
    int EstimatedCompetenceCount = 0,
    int UndefinedNatureCount = 0,
    decimal UndefinedNatureAmount = 0m,
    bool NoCards = false,
    /// <summary>Despesas sem tipo que não têm onde ser classificadas (encargo de fatura, parcela histórica, dívida): ficam fora das fixas e não entram na contagem acima.</summary>
    int UnclassifiableCount = 0);

public sealed record BudgetCompositionSource(
    Guid BudgetId,
    DateOnly Competence,
    Guid TargetId,
    BudgetTargetGroupingType GroupingType,
    Guid? CategoryId,
    decimal Planned,
    IReadOnlyList<BudgetFact> Facts,
    BudgetCoverage Coverage,
    IReadOnlyDictionary<Guid, string> CategoryPaths);
