using Saldo360.Domain.Categories;

namespace Saldo360.Application.Recurrences;

/// <summary>
/// Situação da obrigação mensal. <c>Planned</c> = sem pagamento vinculado; <c>Realized</c> = vínculo confirmado com uma movimentação;
/// <c>NeedsReview</c> = competência fechada/histórica sem evidência de pagamento (não é dívida confirmada nem pagamento).
/// </summary>
public enum ObligationSituation { Planned, Realized, NeedsReview }

/// <summary>Origem do valor vigente da competência: <c>Base</c> = valor da recorrência; <c>Adjustment</c> = exceção/previsão da competência sem confirmação; <c>Confirmed</c> = valor real confirmado (não é pagamento).</summary>
public enum ObligationAmountSource { Base, Adjustment, Confirmed }

/// <summary>
/// Uma obrigação mensal (despesa fixa) numa competência: UM registro por (recorrência, competência) ou por previsão avulsa.
/// O valor previsto já é o da exceção da competência, quando existe (nunca soma).
/// </summary>
public sealed record FixedObligation(
    Guid WorkspaceId, Guid? RecurrenceId, int? OccurrenceNumber, DateOnly Competence, Guid? PlannedTransactionId,
    Guid AccountId, Guid CategoryId, CategoryType Type, ExpenseNature Nature, string Description,
    decimal PlannedAmount, bool IsEstimated, DateOnly DueDate, ObligationSituation Situation, bool IsVirtual,
    decimal? ActualAmount = null, DateOnly? ActualDate = null, Guid? TransactionId = null,
    decimal? BaseAmount = null, ObligationAmountSource AmountSource = ObligationAmountSource.Base, decimal? AdjustedAmount = null,
    Guid? SourceTransactionId = null, string? ConfirmationSource = null)
{
    /// <summary>ValorConsolidado = COALESCE(realizado vinculado, previsto). Previsto e realizado nunca são somados.</summary>
    public decimal Consolidated => ActualAmount ?? PlannedAmount;

    /// <summary>Só obrigações ainda sem pagamento e confirmadas entram no fluxo de caixa futuro.</summary>
    public bool IsPendingCashFlow => Situation == ObligationSituation.Planned;

    /// <summary>Identidade estável do item de agenda: a própria previsão ou, para ocorrência virtual, um id determinístico de (recorrência, competência).</summary>
    public Guid StableId => PlannedTransactionId ?? DeterministicId(RecurrenceId ?? Guid.Empty, Competence);

    public static Guid DeterministicId(Guid recurrenceId, DateOnly competence) =>
        new(System.Security.Cryptography.MD5.HashData(System.Text.Encoding.UTF8.GetBytes($"obligation:{recurrenceId:N}:{competence:yyyy-MM}")));

    public string ReconciliationStatus => Situation switch { ObligationSituation.Realized => "Reconciled", ObligationSituation.NeedsReview => "NeedsReview", _ => "Planned" };
}

/// <summary>Indicadores únicos de despesas fixas, reutilizados por Visão Geral, Agenda, orçamento e projeção.</summary>
public static class FixedObligationIndicators
{
    private static IEnumerable<FixedObligation> Expenses(IEnumerable<FixedObligation> items) => items.Where(i => i.Type == CategoryType.Expense);

    /// <summary>Soma dos pagamentos efetivos vinculados.</summary>
    public static decimal FixasRealizadas(IEnumerable<FixedObligation> items) => Expenses(items).Where(i => i.Situation == ObligationSituation.Realized).Sum(i => i.ActualAmount ?? 0m);

    /// <summary>Soma COALESCE(realizado, previsto) por obrigação; "a revisar" não entra (não é dívida confirmada).</summary>
    public static decimal FixasConsolidadas(IEnumerable<FixedObligation> items) => Expenses(items).Where(i => i.Situation != ObligationSituation.NeedsReview).Sum(i => i.Consolidated);

    /// <summary>Somente o previsto ainda não efetivado (fluxo de caixa futuro).</summary>
    public static decimal FixasPrevistasPendentes(IEnumerable<FixedObligation> items) => Expenses(items).Where(i => i.IsPendingCashFlow).Sum(i => i.PlannedAmount);

    public static int RevisaoPendente(IEnumerable<FixedObligation> items) => items.Count(i => i.Situation == ObligationSituation.NeedsReview);
}
