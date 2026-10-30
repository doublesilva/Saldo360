using System.Net;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Saldo360.Domain.Common;
using Saldo360.Tests.Support;

namespace Saldo360.Tests.Api;

/// <summary>
/// R05: jornadas integradas entre Agenda, previsões/recorrências, orçamento, Visão Geral e objetivos, pelos fluxos reais da API sobre PostgreSQL real e dados
/// sintéticos (sem Pluggy nem OpenAI). As jornadas com a sincronização bancária simulada ficam em <c>PluggyFinancialIntegrationTests.R05Journey</c>.
/// </summary>
[Collection(PostgresCollection.Name)]
[Trait("Category", "CORE")]
[Trait("Area", "Planning")]
[Trait("Area", "Recurrences")]
[Trait("Area", "Budgets")]
[Trait("Area", "Dashboard")]
[Trait("Area", "Goals")]
[Trait("Dependency", "PostgreSQL")]
public sealed class R05IntegratedCycleTests(PostgresFixture postgres) : PostgresApiTestBase(postgres)
{
    private static DateOnly Today => BusinessCalendar.Today(DateTimeOffset.UtcNow);
    private static DateOnly FirstOfMonth => new(Today.Year, Today.Month, 1);
    private static string Competence => Today.ToString("yyyy-MM");

    private sealed record IdDto(Guid Id);
    private sealed record Setup(string Session, WorkspaceDto Workspace, Guid Account, Guid Internet, Guid Income)
    {
        public string Root => $"/api/financial-workspaces/{Workspace.Id}";
    }

    private async Task<Setup> ArrangeAsync(string user)
    {
        var session = await SignInAsync(user);
        var workspace = await CreateWorkspaceAsync(session);
        var root = $"/api/financial-workspaces/{workspace.Id}";
        async Task<Guid> Create(string path, object body) => (await ReadAsync<IdDto>(await SendWithCsrfAsync(HttpMethod.Post, $"{root}/{path}", session, body))).Id;
        var account = await Create("accounts", new { name = "Conta PF", type = "Checking", financialScope = "PF", isPrimary = true, openingBalance = "1000.00", openingBalanceDate = FirstOfMonth.AddMonths(-1) });
        var internet = await Create("categories", new { name = "Internet", type = "Expense" });
        var income = await Create("categories", new { name = "Receitas", type = "Income" });
        return new(session, workspace, account, internet, income);
    }

    private async Task<JsonElement> GetAsync(Setup s, string url)
    {
        var response = await SendAsync(HttpMethod.Get, url, s.Session);
        var body = await response.Content.ReadAsStringAsync();
        Assert.True(response.StatusCode == HttpStatusCode.OK, $"{url}: {(int)response.StatusCode} {body}");
        return JsonDocument.Parse(body).RootElement.Clone();
    }

    private async Task<JsonElement> PostAsync(Setup s, string url, object body, HttpStatusCode expected)
    {
        var response = await SendWithCsrfAsync(HttpMethod.Post, url, s.Session, body);
        var text = await response.Content.ReadAsStringAsync();
        Assert.True(response.StatusCode == expected, $"{url}: {(int)response.StatusCode} {text}");
        return string.IsNullOrEmpty(text) ? default : JsonDocument.Parse(text).RootElement.Clone();
    }

    private static string Text(JsonElement element, params string[] path)
    {
        foreach (var part in path) element = element.GetProperty(part);
        return element.GetString()!;
    }

    private Task<JsonElement> OverviewAsync(Setup s) => GetAsync(s, $"{s.Root}/financial-dashboard/overview?scope=all&competence={Competence}");

    private static string BalanceOf(JsonElement overview, Guid account) =>
        Text(overview.GetProperty("accounts").GetProperty("items").EnumerateArray().Single(a => a.GetProperty("accountId").GetGuid() == account), "balance");

    // ---- Cenário E: previsão e recorrência -------------------------------------------------------------------------------------------

    [Fact]
    public async Task E_ARecurrenceOccurrenceIsPendingInTheAgendaAndBudgetThenRealizedAtADifferentAmountReplacesThePendingEverywhere()
    {
        var s = await ArrangeAsync("r05-e");
        var budget = (await PostAsync(s, $"{s.Root}/budgets", new { competence = Competence }, HttpStatusCode.Created)).GetProperty("id").GetGuid();
        var target = await AddCategoryTargetAsync(s, budget, "500.00");
        var recurrence = (await PostAsync(s, $"{s.Root}/recurrences", new
        {
            accountId = s.Account, categoryId = s.Internet, type = "Expense", description = "Internet", amount = "380.00", isEstimated = false, frequency = "Monthly", interval = 1,
            startDate = FirstOfMonth, dayOfMonth = 1,
        }, HttpStatusCode.Created)).GetProperty("id").GetGuid();
        var materialized = await PostAsync(s, $"{s.Root}/recurrences/{recurrence}/materialize", new { from = FirstOfMonth, through = FirstOfMonth.AddMonths(1).AddDays(-1) }, HttpStatusCode.OK);
        var planned = materialized.GetProperty("items")[0].GetProperty("plannedTransactionId").GetGuid();
        var month = $"from={FirstOfMonth:yyyy-MM-dd}&through={FirstOfMonth.AddMonths(1).AddDays(-1):yyyy-MM-dd}";

        // Pendente (ou vencida): aparece na Agenda e no previsto adicional do orçamento, e o saldo ainda não mudou.
        var agendaBefore = await GetAsync(s, $"{s.Root}/financial-calendar?{month}");
        var pending = agendaBefore.GetProperty("items").EnumerateArray().Single(i => i.GetProperty("description").GetString() == "Internet");
        Assert.Equal(("PlannedTransaction", "380.00"), (Text(pending, "source"), Text(pending, "amount")));
        Assert.Contains(Text(pending, "status"), new[] { "Pending", "Overdue" });
        var before = (await ExecutionAsync(s, budget)).Single(t => t.GetProperty("targetId").GetGuid() == target);
        Assert.Equal(("0.00", "380.00"), (Text(before, "consumed"), Text(before, "committed")));
        var overviewBefore = await OverviewAsync(s);
        var openBefore = overviewBefore.GetProperty("commitments");
        Assert.Equal(1, openBefore.GetProperty("overdue").GetProperty("count").GetInt32() + openBefore.GetProperty("upcoming").GetProperty("count").GetInt32());
        var balanceBefore = BalanceOf(overviewBefore, s.Account);

        // Realiza por um valor diferente do previsto; repetir é idempotente.
        await PostAsync(s, $"{s.Root}/planned-transactions/{planned}/realize", new { amount = "400.00", transactionDate = Today }, HttpStatusCode.Created);
        await PostAsync(s, $"{s.Root}/planned-transactions/{planned}/realize", new { amount = "400.00", transactionDate = Today }, HttpStatusCode.OK);

        var agendaAfter = await GetAsync(s, $"{s.Root}/financial-calendar?{month}");
        var internetItems = agendaAfter.GetProperty("items").EnumerateArray().Where(i => i.GetProperty("description").GetString() == "Internet").ToList();
        var realized = Assert.Single(internetItems); // o realizado substitui o pendente: nunca os dois ativos
        Assert.Equal(("Transaction", "Realized", "400.00"), (Text(realized, "source"), Text(realized, "status"), Text(realized, "amount")));
        var after = (await ExecutionAsync(s, budget)).Single(t => t.GetProperty("targetId").GetGuid() == target);
        Assert.Equal(("400.00", "0.00", "100.00"), (Text(after, "consumed"), Text(after, "committed"), Text(after, "available")));
        var overviewAfter = await OverviewAsync(s);
        var commitmentsAfter = overviewAfter.GetProperty("commitments");
        Assert.Equal(0, commitmentsAfter.GetProperty("overdue").GetProperty("count").GetInt32() + commitmentsAfter.GetProperty("upcoming").GetProperty("count").GetInt32());
        Assert.NotEqual(balanceBefore, BalanceOf(overviewAfter, s.Account)); // o saldo passou a refletir a saída realizada
        await using var db = Db();
        Assert.Equal(1, await db.FinancialTransactions.CountAsync(t => t.WorkspaceId == s.Workspace.Id && !t.IsCancelled && t.Description == "Internet"));
    }

    // ---- Cenário F: objetivo financeiro ------------------------------------------------------------------------------------------------

    [Fact]
    public async Task F_AGoalAllocationExplainsProgressWithoutCreatingBankMovementOrChangingBalanceAndCorrectionsRecalculateIt()
    {
        var s = await ArrangeAsync("r05-f");
        var goal = (await PostAsync(s, $"{s.Root}/financial-goals", new { name = "Viagem", description = (string?)null, goalType = "Travel", targetAmount = "1000.00", targetDate = Today.AddMonths(6), priority = "High" }, HttpStatusCode.Created)).GetProperty("id").GetGuid();
        var overviewBefore = await OverviewAsync(s);
        var balanceBefore = BalanceOf(overviewBefore, s.Account);
        await using (var db = Db()) Assert.Equal(0, await db.FinancialTransactions.CountAsync(t => t.WorkspaceId == s.Workspace.Id));

        await PostAsync(s, $"{s.Root}/financial-goals/{goal}/contributions", new { operationId = Guid.NewGuid(), financialAccountId = s.Account, amount = "600.00", effectiveDate = Today, description = (string?)null }, HttpStatusCode.Created);
        var withdrawal = await PostAsync(s, $"{s.Root}/financial-goals/{goal}/withdrawals", new { operationId = Guid.NewGuid(), financialAccountId = s.Account, amount = "100.00", effectiveDate = Today, description = (string?)null }, HttpStatusCode.Created);

        // Progresso = aportes ativos − retiradas ativas, e a lista de alocações o explica.
        var progress = await GetAsync(s, $"{s.Root}/financial-goals/{goal}/progress");
        Assert.Equal(("500.00", "500.00", "50.00"), (Text(progress, "allocatedAmount"), Text(progress, "remainingToTarget"), Text(progress, "progressPercentage")));
        var allocations = await GetAsync(s, $"{s.Root}/financial-goals/{goal}/allocations");
        Assert.Equal(new[] { "Contribution", "Withdrawal" }, allocations.EnumerateArray().Select(a => Text(a, "operationType")).Order().ToArray());
        // Dashboard: mesmo valor, com o que falta; a alocação não cria movimentação nem altera o saldo (é uma reserva dentro dele).
        var overview = await OverviewAsync(s);
        var item = overview.GetProperty("goals").GetProperty("items").EnumerateArray().Single();
        Assert.Equal(("1000.00", "500.00", "500.00", "50.0", false), (Text(item, "target"), Text(item, "allocated"), Text(item, "remaining"), Text(item, "progressPercent"), item.GetProperty("funded").GetBoolean()));
        Assert.Equal(balanceBefore, BalanceOf(overview, s.Account));
        var summary = await GetAsync(s, $"{s.Root}/financial-goals/summary");
        Assert.Equal(("500.00", "1000.00", "500.00"), (Text(summary, "allocatedToGoals"), Text(summary, "realizedBalance"), Text(summary, "unallocatedRealizedBalance")));
        await using (var db = Db())
        {
            Assert.Equal(0, await db.FinancialTransactions.CountAsync(t => t.WorkspaceId == s.Workspace.Id));
            Assert.Equal(0, await db.FinancialTransfers.CountAsync(t => t.WorkspaceId == s.Workspace.Id));
        }
        var calendar = await GetAsync(s, $"{s.Root}/financial-calendar?from={FirstOfMonth:yyyy-MM-dd}&through={FirstOfMonth.AddMonths(1).AddDays(-1):yyyy-MM-dd}");
        Assert.Equal(("0.00", "0.00"), (Text(calendar, "summary", "realizedIncome"), Text(calendar, "summary", "realizedExpense")));

        // Não se aloca mais do que o saldo não alocado, e cancelar a retirada devolve o valor ao objetivo.
        await PostAsync(s, $"{s.Root}/financial-goals/{goal}/contributions", new { operationId = Guid.NewGuid(), financialAccountId = s.Account, amount = "600.00", effectiveDate = Today, description = (string?)null }, HttpStatusCode.Conflict);
        await PostAsync(s, $"{s.Root}/financial-goals/{goal}/allocations/{Text(withdrawal, "id")}/cancel", new { }, HttpStatusCode.OK);
        var corrected = await GetAsync(s, $"{s.Root}/financial-goals/{goal}/progress");
        Assert.Equal(("600.00", "400.00", "60.00"), (Text(corrected, "allocatedAmount"), Text(corrected, "remainingToTarget"), Text(corrected, "progressPercentage")));

        // O sistema recusa um aporte que passe do alvo; só reduzir o alvo depois da alocação deixa o objetivo acima dele. Aí o restante nunca fica negativo, o percentual
        // passa de 100 e o objetivo conta como atingido, e a tela precisa dizer "acima do alvo" em vez de esconder o excesso.
        await PostAsync(s, $"{s.Root}/financial-goals/{goal}/contributions", new { operationId = Guid.NewGuid(), financialAccountId = s.Account, amount = "500.00", effectiveDate = Today, description = (string?)null }, HttpStatusCode.Conflict);
        var update = await SendWithCsrfAsync(HttpMethod.Put, $"{s.Root}/financial-goals/{goal}", s.Session, new { name = "Viagem", description = (string?)null, goalType = "Travel", targetAmount = "500.00", targetDate = Today.AddMonths(6), priority = "High" });
        Assert.True(update.IsSuccessStatusCode, await update.Content.ReadAsStringAsync());
        var over = await GetAsync(s, $"{s.Root}/financial-goals/{goal}/progress");
        Assert.Equal(("600.00", "0.00", "120.00", true), (Text(over, "allocatedAmount"), Text(over, "remainingToTarget"), Text(over, "progressPercentage"), over.GetProperty("isFunded").GetBoolean()));
        Assert.Equal("1000.00", Text(await GetAsync(s, $"{s.Root}/financial-goals/summary"), "realizedBalance")); // o saldo realizado segue sendo o do banco
    }

    // ---- auxiliares ----------------------------------------------------------------------------------------------------------------------

    private async Task<Guid> AddCategoryTargetAsync(Setup s, Guid budget, string planned)
    {
        var created = await PostAsync(s, $"{s.Root}/budgets/{budget}/targets", new { name = "Internet", groupingType = "Category", categoryId = s.Internet, plannedAmount = planned }, HttpStatusCode.Created);
        return created.GetProperty("targets").EnumerateArray().Single(t => t.GetProperty("groupingType").GetString() == "Category" && !t.GetProperty("isArchived").GetBoolean()).GetProperty("id").GetGuid();
    }

    private async Task<List<JsonElement>> ExecutionAsync(Setup s, Guid budget) =>
        (await GetAsync(s, $"{s.Root}/budgets/{budget}/execution")).GetProperty("targets").EnumerateArray().ToList();
}
