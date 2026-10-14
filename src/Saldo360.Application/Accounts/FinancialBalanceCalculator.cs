namespace Saldo360.Application.Accounts;

/// <summary>Fórmulas monetárias compartilhadas pelo saldo realizado, pela T06.3 e pela evolução da Agenda.</summary>
public static class FinancialBalanceCalculator
{
    public static decimal ApplyMovements(
        decimal balance,
        decimal income,
        decimal expense,
        decimal transferIn = 0m,
        decimal transferOut = 0m) =>
        balance + income - expense + transferIn - transferOut;

    public static decimal Project(decimal realizedBalance, PlannedTotals planned) =>
        ApplyMovements(
            realizedBalance,
            planned.OverdueIncome + planned.PlannedIncome,
            planned.OverdueExpense + planned.PlannedExpense);
}
