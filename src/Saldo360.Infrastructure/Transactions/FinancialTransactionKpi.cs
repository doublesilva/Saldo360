using Saldo360.Domain.Categories;
using Saldo360.Domain.Transactions;

namespace Saldo360.Infrastructure.Transactions;

/// <summary>
/// Regra ÚNICA de como um lançamento entra nos indicadores de receita e despesa (Agenda, Visão Geral, evolução, planejamento). O saldo continua somando
/// todas as entradas e saídas de caixa; os indicadores separam o que é receita do que é devolução:
/// <list type="bullet">
/// <item><see cref="Row.Revenue"/> = receita de verdade: entrada que NÃO é devolução bancária confirmada;</item>
/// <item><see cref="Row.NetExpense"/> = despesa menos devoluções: uma devolução reduz a despesa na data do crédito (nunca vira receita);</item>
/// <item><see cref="Row.Refund"/> = valor devolvido no lançamento (informativo).</item>
/// </list>
/// Como Revenue − NetExpense = Entradas − Saídas, a identidade do saldo se mantém em qualquer recorte.
/// </summary>
internal static class FinancialTransactionKpi
{
    internal sealed class Row
    {
        public Guid AccountId { get; init; }
        public DateOnly Date { get; init; }
        public decimal Revenue { get; init; }
        public decimal NetExpense { get; init; }
        public decimal Refund { get; init; }
        public decimal CardPayment { get; init; }
    }

    public static IQueryable<Row> Project(IQueryable<FinancialTransaction> transactions) =>
        transactions.Select(t => new Row
        {
            AccountId = t.AccountId,
            Date = t.TransactionDate,
            Revenue = t.Type == CategoryType.Income && t.Origin != FinancialTransactionOrigin.PluggyRefund ? t.Amount : 0m,
            NetExpense = t.Type == CategoryType.Expense ? t.Amount : t.Origin == FinancialTransactionOrigin.PluggyRefund ? -t.Amount : 0m,
            Refund = t.Origin == FinancialTransactionOrigin.PluggyRefund ? t.Amount : 0m,
            CardPayment = t.Origin == FinancialTransactionOrigin.CreditCardInvoicePayment ? t.Amount : 0m,
        });
}
