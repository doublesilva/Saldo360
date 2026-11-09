using System.Net;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Saldo360.Application.Pluggy;
using Saldo360.Domain.Pluggy;

namespace Saldo360.Tests.Pluggy;

// T13.3 (Solar): débito POSTED de operação de crédito → revisão → reconhecimento pela série decidida pelo usuário → lançamento único → vínculo com a obrigação do mês.
public sealed partial class PluggyFinancialIntegrationTests
{
    private const decimal CreditInstallment = 748.31m;
    private const string CreditDescription = "Saída CREDIARIO 0123";

    /// <summary>Dias atrás de uma saída mensal (dia do mês igual, 3 dias antes de hoje, mês a mês).</summary>
    private static int MonthlyAgo(int monthsBack) => Today.DayNumber - Today.AddMonths(-monthsBack).AddDays(-3).DayNumber;

    private static PluggyTransaction CreditDebit(string id, int monthsBack, decimal amount = CreditInstallment) =>
        Tx(id, BankA, MonthlyAgo(monthsBack), -amount, operation: "OPERACAO_CREDITO", description: CreditDescription, category: null);

    private async Task<(Ctx C, Guid Account)> ArrangeCreditSeriesAsync(string user, IEnumerable<PluggyTransaction> transactions)
    {
        var c = await ArrangeAsync(user, seed: false);
        Pluggy.TransactionsByAccount[BankA] = [.. transactions];
        var account = (await LinkBankAsync(c)).GetProperty("link").GetProperty("financialAccountId").GetGuid();
        await SyncAsync(c);
        return (c, account);
    }

    private async Task ResolveAsExpenseAsync(Ctx c, params string[] descriptionDates)
    {
        var items = (await ReviewAsync(c)).GetProperty("items").EnumerateArray().ToList();
        foreach (var date in descriptionDates)
        {
            var item = items.Single(i => i.GetProperty("date").GetString() == date);
            var response = await PostAsync(c, $"{Integration(c.Workspace)}/review/{item.GetProperty("postingId").GetGuid()}/resolve", new { resolution = "AsExpense" });
            Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        }
    }

    private async Task<Guid> SolarRecurrenceAsync(Ctx c, Guid account)
    {
        var category = await PostAsync(c, $"/api/financial-workspaces/{c.Workspace}/categories", new { name = "Crediário", type = "Expense" });
        Assert.Equal(HttpStatusCode.Created, category.StatusCode);
        var due = Today.AddMonths(0).AddDays(-3);
        var response = await PostAsync(c, $"/api/financial-workspaces/{c.Workspace}/recurrences", new
        {
            accountId = account, categoryId = (await JsonOf(category)).GetProperty("id").GetGuid(), type = "Expense", description = "Crediário Solar", amount = "750.00", isEstimated = false,
            frequency = "Monthly", interval = 1, startDate = due, dayOfMonth = due.Day,
        });
        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        return (await JsonOf(response)).GetProperty("id").GetGuid();
    }

    [Fact]
    public async Task ACreditOperationDebitWithAUserDecidedSeriesIsRecognizedOnceAndSettlesTheMonthObligationWithoutAnotherDebit()
    {
        // 4 meses anteriores + o mês atual, todos "POSTED": sem decisão do usuário ficam em revisão (operação de crédito pode ser desembolso ou transferência).
        var (c, account) = await ArrangeCreditSeriesAsync("t133-solar", [CreditDebit("s4", 4), CreditDebit("s3", 3), CreditDebit("s2", 2), CreditDebit("s1", 1), CreditDebit("s0", 0)]);
        Assert.Equal(5, (await ReviewAsync(c)).GetProperty("items").EnumerateArray().Count(i => i.GetProperty("reason").GetString() == "CreditOperation"));
        var recurrence = await SolarRecurrenceAsync(c, account);

        // O usuário decide os três mais antigos como despesa; os dois seguintes passam a ser reconhecidos pela própria série.
        await ResolveAsExpenseAsync(c, Today.AddMonths(-4).AddDays(-3).ToString("yyyy-MM-dd"), Today.AddMonths(-3).AddDays(-3).ToString("yyyy-MM-dd"), Today.AddMonths(-2).AddDays(-3).ToString("yyyy-MM-dd"));
        await SyncAsync(c);

        await using var db = Db();
        var pendingReview = (await ReviewAsync(c)).GetProperty("items").EnumerateArray().Count(i => i.GetProperty("reason").GetString() == "CreditOperation");
        var debits = await db.FinancialTransactions.CountAsync(t => t.Amount == CreditInstallment);
        var settlements = await db.FinancialObligationSettlements.Where(s => s.RecurrenceId == recurrence && s.RemovedAt == null).ToListAsync();
        var baseAmount = await db.FinancialRecurrences.Where(r => r.Id == recurrence).Select(r => r.Amount).SingleAsync();

        Assert.Equal(0, pendingReview);                          // nada ficou preso em revisão
        Assert.Equal(5, debits);                                 // um lançamento por movimentação do banco; a quitação não criou outro débito
        var settlement = Assert.Single(settlements);             // a obrigação do mês foi quitada pela movimentação já importada
        Assert.True(await db.FinancialTransactions.AnyAsync(t => t.Id == settlement.FinancialTransactionId && t.Amount == CreditInstallment));
        Assert.Equal(750.00m, baseAmount);                       // o valor-base da recorrência não muda

        // Reprocessar não duplica nada.
        await SyncAsync(c);
        await using var again = Db();
        Assert.Equal((5, 1), (await again.FinancialTransactions.CountAsync(t => t.Amount == CreditInstallment), await again.FinancialObligationSettlements.CountAsync(s => s.RecurrenceId == recurrence && s.RemovedAt == null)));
    }

    [Fact]
    public async Task AnAmbiguousOrNewCreditOperationStaysInReviewAndSettlesNothing()
    {
        // Série curta (só duas decisões do usuário) e uma saída de valor novo: nenhuma é reconhecida.
        var (c, account) = await ArrangeCreditSeriesAsync("t133-ambiguo", [CreditDebit("a3", 3), CreditDebit("a2", 2), CreditDebit("a1", 1), CreditDebit("a0", 0, amount: 801.10m)]);
        var recurrence = await SolarRecurrenceAsync(c, account);
        await ResolveAsExpenseAsync(c, Today.AddMonths(-3).AddDays(-3).ToString("yyyy-MM-dd"), Today.AddMonths(-2).AddDays(-3).ToString("yyyy-MM-dd"));
        await SyncAsync(c);

        await using var db = Db();
        var stillInReview = (await ReviewAsync(c)).GetProperty("items").EnumerateArray().Where(i => i.GetProperty("reason").GetString() == "CreditOperation").Select(i => i.GetProperty("amount").GetString()).Order().ToList();

        Assert.Equal(["748.31", "801.10"], stillInReview);   // o 1º mês da série (só 2 meses de histórico) e o valor novo continuam em revisão
        Assert.Equal(0, await db.FinancialObligationSettlements.CountAsync(s => s.RecurrenceId == recurrence));
    }
}
