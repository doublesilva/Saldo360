namespace Saldo360.Application.Budgets;

/// <summary>Parcela de financiamento de saldo de fatura: obrigação de pagamento (fatura e caixa) que NÃO é consumo do orçamento. Fora de <see cref="BudgetFact"/> de propósito: nenhum consumidor do orçamento a soma por engano.</summary>
public sealed record InvoiceFinancingLine(Guid InstallmentId, Guid PurchaseId, Guid CardId, string CardName, DateOnly DueDate, decimal Amount, string Competence, string Description, Guid? CategoryId);

public interface IFinancialBudgetExecutionRepository
{
    /// <summary>
    /// Reads the active budget, its active targets and category aggregates in one read-only repeatable-read snapshot.
    /// Returns null when the budget does not exist in the family or is archived.
    /// </summary>
    Task<FinancialBudgetExecutionSource?> GetAsync(
        Guid workspaceId, Guid budgetId, CancellationToken cancellationToken);

    /// <summary>Todos os registros de despesa do intervalo [first, through), pela mesma lista do orçamento, com os caminhos "Categoria / Subcategoria". Somente leitura.</summary>
    Task<(IReadOnlyList<BudgetFact> Facts, IReadOnlyDictionary<Guid, string> CategoryPaths)> ReadFactsAsync(Guid workspaceId, DateOnly first, DateOnly through, CancellationToken cancellationToken);

    /// <summary>Parcelas de financiamento de fatura do intervalo [first, through), por competência da fatura. Não entram no consumo do orçamento. Somente leitura.</summary>
    Task<IReadOnlyList<InvoiceFinancingLine>> ReadInvoiceFinancingAsync(Guid workspaceId, DateOnly first, DateOnly through, CancellationToken cancellationToken);

    /// <summary>Os registros que compõem uma meta, pela MESMA lista e regra de cobertura do resumo. Null se o orçamento ou a meta não existirem (ou estiverem arquivados).</summary>
    Task<BudgetCompositionSource?> GetCompositionAsync(
        Guid workspaceId, Guid budgetId, Guid targetId, CancellationToken cancellationToken);

    /// <summary>T12.1: execução de UMA competência sobre as metas efetivas (exceção, padrão vigente ou registro histórico), com ou sem orçamento mensal materializado. Somente leitura.</summary>
    Task<FinancialBudgetExecutionSource?> GetEffectiveAsync(Guid workspaceId, DateOnly competence, CancellationToken cancellationToken);

    /// <summary>T12.1: composição de uma meta efetiva da competência (herdada ou não). Null se a meta não estiver definida naquele mês.</summary>
    Task<BudgetCompositionSource?> GetEffectiveCompositionAsync(Guid workspaceId, DateOnly competence, Guid targetId, CancellationToken cancellationToken);
}

