using System.Net;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Npgsql;
using Saldo360.Tests.Support;

namespace Saldo360.Tests.Api;

/// <summary>
/// T09.3.3: conta principal do orçamento (uma por workspace, só bancária e ativa, troca atômica) e conta pagadora padrão dos
/// cartões. A principal é só a seleção padrão: não altera saldo, movimentação nem a conta pagadora dos cartões.
/// </summary>
[Collection(PostgresCollection.Name)]
[Trait("Category", "CORE")]
[Trait("Area", "Accounts")]
[Trait("Dependency", "PostgreSQL")]
public sealed class PrimaryAccountAndCardPayerTests(PostgresFixture postgres) : PostgresApiTestBase(postgres)
{
    private string _owner = null!;
    private string _outsider = null!;
    private WorkspaceDto _w = null!;
    private WorkspaceDto _other = null!;

    private string Accounts => $"/api/financial-workspaces/{_w.Id}/accounts";

    private string Cards => $"/api/financial-workspaces/{_w.Id}/credit-cards";

    private async Task ArrangeAsync()
    {
        _owner = await SignInAsync("principal-dona");
        _outsider = await SignInAsync("principal-estranho");
        _w = await CreateWorkspaceAsync(_owner, "Orçamento");
        _other = await CreateWorkspaceAsync(_outsider, "Outro orçamento");
    }

    private async Task<JsonElement> CreateAccountAsync(string name, string type = "Checking", string scope = "PF", bool? primary = null, WorkspaceDto? workspace = null, string? session = null,
        decimal? opening = null)
    {
        var response = await SendWithCsrfAsync(HttpMethod.Post, $"/api/financial-workspaces/{(workspace ?? _w).Id}/accounts", session ?? _owner, new
        {
            name, type, financialScope = scope, isPrimary = primary, openingBalance = opening, openingBalanceDate = opening is null ? (DateOnly?)null : new DateOnly(2026, 9, 1),
        });
        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        return await ReadAsync<JsonElement>(response);
    }

    private async Task<HttpResponseMessage> SetPrimaryAsync(Guid account, WorkspaceDto? workspace = null, string? session = null) =>
        await SendWithCsrfAsync(HttpMethod.Put, $"/api/financial-workspaces/{(workspace ?? _w).Id}/accounts/{account}/primary", session ?? _owner);

    private async Task<List<Guid>> PrimaryIdsAsync(Guid? workspace = null)
    {
        await using var db = Db();
        return await db.FinancialAccounts.AsNoTracking().Where(a => a.WorkspaceId == (workspace ?? _w.Id) && a.IsPrimary).Select(a => a.Id).ToListAsync();
    }

    private async Task<JsonElement> ListAccountsAsync(bool includeInactive = true) =>
        await ReadAsync<JsonElement>(await SendAsync(HttpMethod.Get, Accounts + (includeInactive ? "?includeInactive=true" : string.Empty), _owner));

    private async Task<JsonElement> CreateCardAsync(string name, Guid? payer = null, string? expectError = null)
    {
        var response = await SendWithCsrfAsync(HttpMethod.Post, Cards, _owner, new { name, issuer = "Banco", creditLimit = "1000.00", closingDay = 5, dueDay = 12, defaultPaymentAccountId = payer });
        if (expectError is not null)
        {
            Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
            Assert.Contains(expectError, await response.Content.ReadAsStringAsync());
            return default;
        }

        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        return await ReadAsync<JsonElement>(response);
    }

    // ---- 1 e 2: primeira conta principal e substituição ------------------------------------------------------------------

    [Fact]
    public async Task TheFirstPrimaryAccountIsDefinedAndReplacedAtomicallyLeavingExactlyOne()
    {
        await ArrangeAsync();
        var a = (await CreateAccountAsync("Itaú PF")).GetProperty("id").GetGuid();
        var b = (await CreateAccountAsync("Nubank PJ", scope: "PJ")).GetProperty("id").GetGuid();
        Assert.Empty(await PrimaryIdsAsync());

        var first = await SetPrimaryAsync(a);
        Assert.Equal(HttpStatusCode.OK, first.StatusCode);
        Assert.True((await ReadAsync<JsonElement>(first)).GetProperty("isPrimary").GetBoolean());
        Assert.Equal([a], await PrimaryIdsAsync());

        Assert.Equal(HttpStatusCode.OK, (await SetPrimaryAsync(b)).StatusCode);
        Assert.Equal([b], await PrimaryIdsAsync());
        var flags = (await ListAccountsAsync()).EnumerateArray().ToDictionary(x => x.GetProperty("id").GetGuid(), x => x.GetProperty("isPrimary").GetBoolean());
        Assert.False(flags[a]);
        Assert.True(flags[b]);

        Assert.Equal(HttpStatusCode.OK, (await SetPrimaryAsync(b)).StatusCode); // idempotente
        Assert.Equal([b], await PrimaryIdsAsync());
    }

    [Fact]
    public async Task CreatingOrEditingAnAccountAsPrimaryReplacesThePreviousOne_AndAnOmittedFlagNeverChangesIt()
    {
        await ArrangeAsync();
        var a = (await CreateAccountAsync("Itaú PF", primary: true)).GetProperty("id").GetGuid();
        Assert.Equal([a], await PrimaryIdsAsync());
        var b = (await CreateAccountAsync("Itaú PJ", scope: "PJ", primary: true)).GetProperty("id").GetGuid();
        Assert.Equal([b], await PrimaryIdsAsync());

        // Edição comum, sem o campo isPrimary: a condição de principal permanece.
        var edit = await SendWithCsrfAsync(HttpMethod.Put, $"{Accounts}/{b}", _owner, new { name = "Itaú PJ renomeada", type = "Checking", financialScope = "PJ", isActive = true });
        Assert.Equal(HttpStatusCode.OK, edit.StatusCode);
        Assert.Equal([b], await PrimaryIdsAsync());

        // Edição com isPrimary=true em outra conta troca a principal; isPrimary=false remove sem substituir.
        var promote = await SendWithCsrfAsync(HttpMethod.Put, $"{Accounts}/{a}", _owner, new { name = "Itaú PF", type = "Checking", financialScope = "PF", isActive = true, isPrimary = true });
        Assert.Equal(HttpStatusCode.OK, promote.StatusCode);
        Assert.Equal([a], await PrimaryIdsAsync());
        var clear = await SendWithCsrfAsync(HttpMethod.Put, $"{Accounts}/{a}", _owner, new { name = "Itaú PF", type = "Checking", financialScope = "PF", isActive = true, isPrimary = false });
        Assert.Equal(HttpStatusCode.OK, clear.StatusCode);
        Assert.Empty(await PrimaryIdsAsync());
    }

    // ---- 3: concorrência -------------------------------------------------------------------------------------------------

    [Fact]
    public async Task ConcurrentReplacementsSerializeAndLeaveExactlyOnePrimary()
    {
        await ArrangeAsync();
        var ids = new List<Guid>();
        for (var i = 0; i < 6; i++) ids.Add((await CreateAccountAsync($"Conta {i}")).GetProperty("id").GetGuid());
        Assert.Equal(HttpStatusCode.OK, (await SetPrimaryAsync(ids[0])).StatusCode);

        var responses = await Task.WhenAll(ids.Select(id => SetPrimaryAsync(id)));

        Assert.All(responses, response => Assert.Equal(HttpStatusCode.OK, response.StatusCode));
        var primaries = await PrimaryIdsAsync();
        Assert.Single(primaries);
        Assert.Contains(primaries[0], ids);
    }

    // ---- 4: cartão (e conta que não é bancária) não pode ser principal ---------------------------------------------------

    [Fact]
    public async Task OnlyAnActiveBankAccountCanBePrimary_NeverCashOrAnInactiveAccount()
    {
        await ArrangeAsync();
        var cash = (await CreateAccountAsync("Carteira", type: "Cash")).GetProperty("id").GetGuid();
        var savings = (await CreateAccountAsync("Poupança", type: "Savings")).GetProperty("id").GetGuid();
        var inactive = (await CreateAccountAsync("Antiga")).GetProperty("id").GetGuid();
        Assert.Equal(HttpStatusCode.OK, (await SendWithCsrfAsync(HttpMethod.Put, $"{Accounts}/{inactive}", _owner,
            new { name = "Antiga", type = "Checking", financialScope = "PF", isActive = false })).StatusCode);

        Assert.Equal(HttpStatusCode.BadRequest, (await SetPrimaryAsync(cash)).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await SetPrimaryAsync(inactive)).StatusCode);
        var createCash = await SendWithCsrfAsync(HttpMethod.Post, Accounts, _owner, new { name = "Caixa", type = "Cash", financialScope = "PF", isPrimary = true });
        Assert.Equal(HttpStatusCode.BadRequest, createCash.StatusCode);
        Assert.Contains("isPrimary", await createCash.Content.ReadAsStringAsync());
        Assert.Empty(await PrimaryIdsAsync());

        Assert.Equal(HttpStatusCode.OK, (await SetPrimaryAsync(savings)).StatusCode);
        // Um cartão não é uma conta financeira: não existe rota nem identificador de conta que o aceite como principal.
        var card = (await CreateCardAsync("Cartão")).GetProperty("id").GetGuid();
        Assert.Equal(HttpStatusCode.NotFound, (await SetPrimaryAsync(card)).StatusCode);
        Assert.Equal([savings], await PrimaryIdsAsync());
    }

    [Fact]
    public async Task TheDatabaseItselfRefusesTwoPrimariesOrANonBankPrimary()
    {
        await ArrangeAsync();
        var a = (await CreateAccountAsync("A", primary: true)).GetProperty("id").GetGuid();
        var b = (await CreateAccountAsync("B")).GetProperty("id").GetGuid();
        var cash = (await CreateAccountAsync("Carteira", type: "Cash")).GetProperty("id").GetGuid();
        await using var db = Db();

        var second = await Assert.ThrowsAsync<PostgresException>(() => db.Database.ExecuteSqlInterpolatedAsync($"UPDATE financial_accounts SET is_primary = true WHERE id = {b}"));
        Assert.Equal("23505", second.SqlState);
        var nonBank = await Assert.ThrowsAsync<PostgresException>(() => db.Database.ExecuteSqlInterpolatedAsync($"UPDATE financial_accounts SET is_primary = true WHERE id = {cash}"));
        Assert.Equal("23514", nonBank.SqlState);
        var inactivePrimary = await Assert.ThrowsAsync<PostgresException>(() => db.Database.ExecuteSqlInterpolatedAsync($"UPDATE financial_accounts SET is_active = false WHERE id = {a}"));
        Assert.Equal("23514", inactivePrimary.SqlState);
    }

    // ---- 5: isolamento entre workspaces ----------------------------------------------------------------------------------

    [Fact]
    public async Task EachWorkspaceHasItsOwnPrimary_AndNobodyCanTouchAnotherWorkspacesAccounts()
    {
        await ArrangeAsync();
        var mine = (await CreateAccountAsync("Minha")).GetProperty("id").GetGuid();
        var theirs = (await CreateAccountAsync("Deles", workspace: _other, session: _outsider)).GetProperty("id").GetGuid();

        Assert.Equal(HttpStatusCode.OK, (await SetPrimaryAsync(mine)).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await SetPrimaryAsync(theirs, _other, _outsider)).StatusCode);
        Assert.Equal([mine], await PrimaryIdsAsync(_w.Id));
        Assert.Equal([theirs], await PrimaryIdsAsync(_other.Id));

        // Cruzado: nem pela rota do outro workspace, nem com a conta alheia na minha rota.
        Assert.Equal(HttpStatusCode.NotFound, (await SetPrimaryAsync(theirs)).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await SetPrimaryAsync(mine, _other, _owner)).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await SendWithCsrfAsync(HttpMethod.Delete, $"{Accounts}/{theirs}/primary", _owner)).StatusCode);
        Assert.Equal([mine], await PrimaryIdsAsync(_w.Id));
        Assert.Equal([theirs], await PrimaryIdsAsync(_other.Id));

        // Conta de outro workspace nunca serve de substituta nem de pagadora de cartão.
        var foreign = await SendWithCsrfAsync(HttpMethod.Put, $"{Accounts}/{mine}", _owner,
            new { name = "Minha", type = "Checking", financialScope = "PF", isActive = false, replacementPrimaryAccountId = theirs });
        Assert.Equal(HttpStatusCode.BadRequest, foreign.StatusCode);
        await CreateCardAsync("Cartão", theirs, "defaultPaymentAccountId");
        Assert.Equal([mine], await PrimaryIdsAsync(_w.Id));
    }

    // ---- 12: conta principal desativada ----------------------------------------------------------------------------------

    [Fact]
    public async Task ThePrimaryAccountCannotBeDeactivatedWithoutAReplacementOrAnExplicitRemoval()
    {
        await ArrangeAsync();
        var a = (await CreateAccountAsync("Principal", primary: true)).GetProperty("id").GetGuid();
        var b = (await CreateAccountAsync("Reserva")).GetProperty("id").GetGuid();
        var cash = (await CreateAccountAsync("Carteira", type: "Cash")).GetProperty("id").GetGuid();
        object Deactivate(object? extra = null) => new Dictionary<string, object?>
            { ["name"] = "Principal", ["type"] = "Checking", ["financialScope"] = "PF", ["isActive"] = false }
            .Concat((extra as IDictionary<string, object?>) ?? new Dictionary<string, object?>()).ToDictionary(p => p.Key, p => p.Value);

        var bare = await SendWithCsrfAsync(HttpMethod.Put, $"{Accounts}/{a}", _owner, Deactivate());
        Assert.Equal(HttpStatusCode.BadRequest, bare.StatusCode);
        Assert.Contains("replacementPrimaryAccountId", await bare.Content.ReadAsStringAsync());
        // Mudar a conta principal para não bancária segue a mesma regra.
        var toCash = await SendWithCsrfAsync(HttpMethod.Put, $"{Accounts}/{a}", _owner, new { name = "Principal", type = "Cash", financialScope = "PF", isActive = true });
        Assert.Equal(HttpStatusCode.BadRequest, toCash.StatusCode);
        // Substituta inválida (dinheiro) ou a própria conta: nada muda (atomicidade).
        var badReplacement = await SendWithCsrfAsync(HttpMethod.Put, $"{Accounts}/{a}", _owner, Deactivate(new Dictionary<string, object?> { ["replacementPrimaryAccountId"] = cash }));
        Assert.Equal(HttpStatusCode.BadRequest, badReplacement.StatusCode);
        var selfReplacement = await SendWithCsrfAsync(HttpMethod.Put, $"{Accounts}/{a}", _owner, Deactivate(new Dictionary<string, object?> { ["replacementPrimaryAccountId"] = a }));
        Assert.Equal(HttpStatusCode.BadRequest, selfReplacement.StatusCode);
        await using (var db = Db())
        {
            var stored = await db.FinancialAccounts.AsNoTracking().SingleAsync(x => x.Id == a);
            Assert.True(stored.IsActive);
            Assert.True(stored.IsPrimary);
        }

        // Com substituta: a desativação e a nova principal acontecem juntas.
        var withReplacement = await SendWithCsrfAsync(HttpMethod.Put, $"{Accounts}/{a}", _owner, Deactivate(new Dictionary<string, object?> { ["replacementPrimaryAccountId"] = b }));
        Assert.Equal(HttpStatusCode.OK, withReplacement.StatusCode);
        var afterReplacement = await ListAccountsAsync();
        Assert.Equal((false, false, true), (Flag(afterReplacement, a, "isActive"), Flag(afterReplacement, a, "isPrimary"), Flag(afterReplacement, b, "isPrimary")));
        Assert.Equal([b], await PrimaryIdsAsync());

        // Remoção explícita: a conta principal deixa de ser principal e é desativada, sem substituta.
        var clearAndDeactivate = await SendWithCsrfAsync(HttpMethod.Put, $"{Accounts}/{b}", _owner,
            new { name = "Reserva", type = "Checking", financialScope = "PF", isActive = false, isPrimary = false });
        Assert.Equal(HttpStatusCode.OK, clearAndDeactivate.StatusCode);
        Assert.Empty(await PrimaryIdsAsync());
        // Uma conta desativada nunca volta a ser principal sem reativar.
        Assert.Equal(HttpStatusCode.BadRequest, (await SetPrimaryAsync(b)).StatusCode);

        static bool Flag(JsonElement list, Guid id, string name) =>
            list.EnumerateArray().Single(x => x.GetProperty("id").GetGuid() == id).GetProperty(name).GetBoolean();
    }

    [Fact]
    public async Task RemovingThePrimaryConditionIsExplicitAndIdempotent()
    {
        await ArrangeAsync();
        var a = (await CreateAccountAsync("Itaú PF", primary: true)).GetProperty("id").GetGuid();

        var first = await SendWithCsrfAsync(HttpMethod.Delete, $"{Accounts}/{a}/primary", _owner);
        Assert.Equal(HttpStatusCode.OK, first.StatusCode);
        Assert.False((await ReadAsync<JsonElement>(first)).GetProperty("isPrimary").GetBoolean());
        Assert.Equal(HttpStatusCode.OK, (await SendWithCsrfAsync(HttpMethod.Delete, $"{Accounts}/{a}/primary", _owner)).StatusCode);
        Assert.Empty(await PrimaryIdsAsync());
    }

    // ---- 10: trocar a principal não altera saldos, movimentações nem pagadoras --------------------------------------------

    [Fact]
    public async Task ChangingThePrimaryNeverChangesBalancesMovementsOrCardPayers()
    {
        await ArrangeAsync();
        var a = (await CreateAccountAsync("Itaú PF", opening: 1000m)).GetProperty("id").GetGuid();
        var b = (await CreateAccountAsync("Nubank PJ", scope: "PJ", opening: 250m)).GetProperty("id").GetGuid();
        var category = (await ReadAsync<JsonElement>(await SendWithCsrfAsync(HttpMethod.Post, $"/api/financial-workspaces/{_w.Id}/categories", _owner, new { name = "Mercado", type = "Expense" }))).GetProperty("id").GetGuid();
        Assert.Equal(HttpStatusCode.Created, (await SendWithCsrfAsync(HttpMethod.Post, $"/api/financial-workspaces/{_w.Id}/transactions", _owner,
            new { accountId = a, categoryId = category, type = "Expense", description = "Compra", amount = "100.00", transactionDate = new DateOnly(2026, 9, 5) })).StatusCode);
        var card = (await CreateCardAsync("Cartão PF", a)).GetProperty("id").GetGuid();
        var before = await FingerprintAsync(card);

        Assert.Equal(HttpStatusCode.OK, (await SetPrimaryAsync(a)).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await SetPrimaryAsync(b)).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await SendWithCsrfAsync(HttpMethod.Delete, $"{Accounts}/{b}/primary", _owner)).StatusCode);

        Assert.Equal(before, await FingerprintAsync(card));
    }

    private async Task<string> FingerprintAsync(Guid card)
    {
        var balances = new List<string>();
        foreach (var account in (await ListAccountsAsync()).EnumerateArray().OrderBy(x => x.GetProperty("name").GetString()))
        {
            var balance = await ReadAsync<JsonElement>(await SendAsync(HttpMethod.Get, $"{Accounts}/{account.GetProperty("id").GetGuid()}/balance?asOf=2026-09-30", _owner));
            balances.Add(balance.GetProperty("balance").GetString()!);
        }

        await using var db = Db();
        var transactions = await db.FinancialTransactions.AsNoTracking().Where(t => t.WorkspaceId == _w.Id).OrderBy(t => t.Amount).Select(t => $"{t.AccountId}:{t.Amount}:{t.IsCancelled}").ToListAsync();
        var transfers = await db.FinancialTransfers.CountAsync(t => t.WorkspaceId == _w.Id);
        var payers = await db.FinancialCreditCards.AsNoTracking().Where(c => c.Id == card).Select(c => c.DefaultPaymentAccountId).SingleAsync();
        var consolidated = await ReadAsync<JsonElement>(await SendAsync(HttpMethod.Get, $"/api/financial-workspaces/{_w.Id}/financial-calendar/balances?from=2026-09-01&through=2026-09-30", _owner));
        return string.Join("|", balances) + "#" + string.Join(",", transactions) + "#" + transfers + "#" + payers + "#" + consolidated.GetRawText();
    }

    // ---- 11: Dashboard e Agenda ------------------------------------------------------------------------------------------

    [Fact]
    public async Task DashboardFlagsThePrimaryAccountAndTheConsolidatedViewsDoNotChangeWhenItIsDefined()
    {
        await ArrangeAsync();
        var a = (await CreateAccountAsync("Itaú PF", opening: 1000m)).GetProperty("id").GetGuid();
        await CreateAccountAsync("Itaú PJ", scope: "PJ", opening: 300m);
        var url = $"/api/financial-workspaces/{_w.Id}/financial-dashboard?competence=2026-09&months=3";
        var before = await ReadAsync<JsonElement>(await SendAsync(HttpMethod.Get, url, _owner));
        Assert.All(before.GetProperty("position").GetProperty("accounts").EnumerateArray(), x => Assert.False(x.GetProperty("isPrimary").GetBoolean()));

        Assert.Equal(HttpStatusCode.OK, (await SetPrimaryAsync(a)).StatusCode);

        var after = await ReadAsync<JsonElement>(await SendAsync(HttpMethod.Get, url, _owner));
        var flags = after.GetProperty("position").GetProperty("accounts").EnumerateArray().ToDictionary(x => x.GetProperty("name").GetString()!, x => x.GetProperty("isPrimary").GetBoolean());
        Assert.Equal((true, false), (flags["Itaú PF"], flags["Itaú PJ"]));
        // O consolidado (saldo, fluxo, indicadores e cobertura) é idêntico: só o indicador de principal muda.
        foreach (var section in new[] { "cashFlow", "indicators", "coverage" })
            Assert.Equal(before.GetProperty(section).GetRawText(), after.GetProperty(section).GetRawText());
        Assert.Equal(before.GetProperty("position").GetProperty("realizedBalance").GetString(), after.GetProperty("position").GetProperty("realizedBalance").GetString());
    }

    // ---- 6, 8 e 9: conta pagadora dos cartões ----------------------------------------------------------------------------

    [Fact]
    public async Task EachCardHasItsOwnPayerAndOnlyAnActiveBankAccountOfTheWorkspaceIsAccepted()
    {
        await ArrangeAsync();
        var pf = (await CreateAccountAsync("Itaú PF")).GetProperty("id").GetGuid();
        var pj = (await CreateAccountAsync("Itaú PJ", scope: "PJ")).GetProperty("id").GetGuid();
        var cash = (await CreateAccountAsync("Carteira", type: "Cash")).GetProperty("id").GetGuid();
        var inactive = (await CreateAccountAsync("Antiga")).GetProperty("id").GetGuid();
        Assert.Equal(HttpStatusCode.OK, (await SendWithCsrfAsync(HttpMethod.Put, $"{Accounts}/{inactive}", _owner,
            new { name = "Antiga", type = "Checking", financialScope = "PF", isActive = false })).StatusCode);

        var personal = await CreateCardAsync("Cartão PF", pf);
        var business = await CreateCardAsync("Cartão PJ", pj);
        Assert.Equal((pf, pj), (personal.GetProperty("defaultPaymentAccountId").GetGuid(), business.GetProperty("defaultPaymentAccountId").GetGuid()));
        var unset = await CreateCardAsync("Sem pagadora");
        Assert.Equal(JsonValueKind.Null, unset.GetProperty("defaultPaymentAccountId").ValueKind);

        await CreateCardAsync("Cartão com dinheiro", cash, "defaultPaymentAccountId");
        await CreateCardAsync("Cartão com conta inativa", inactive, "defaultPaymentAccountId");
        await CreateCardAsync("Cartão com conta inexistente", Guid.NewGuid(), "defaultPaymentAccountId");
    }

    [Fact]
    public async Task APersonalAccountCanPayABusinessCardAndThePayerCanBeChangedWithoutTouchingAnything()
    {
        await ArrangeAsync();
        var pf = (await CreateAccountAsync("Itaú PF", scope: "PF")).GetProperty("id").GetGuid();
        var pj = (await CreateAccountAsync("Itaú PJ", scope: "PJ")).GetProperty("id").GetGuid();
        var card = (await CreateCardAsync("Itaú Empresas Visa", pf)).GetProperty("id").GetGuid();

        var change = await SendWithCsrfAsync(HttpMethod.Put, $"{Cards}/{card}", _owner,
            new { name = "Itaú Empresas Visa", issuer = "Banco", creditLimit = "1000.00", closingDay = 5, dueDay = 12, defaultPaymentAccountId = pj });
        Assert.Equal(HttpStatusCode.OK, change.StatusCode);
        Assert.Equal(pj, (await ReadAsync<JsonElement>(change)).GetProperty("defaultPaymentAccountId").GetGuid());
        var back = await SendWithCsrfAsync(HttpMethod.Put, $"{Cards}/{card}", _owner,
            new { name = "Itaú Empresas Visa", issuer = "Banco", creditLimit = "1000.00", closingDay = 5, dueDay = 12, defaultPaymentAccountId = pf });
        Assert.Equal(pf, (await ReadAsync<JsonElement>(back)).GetProperty("defaultPaymentAccountId").GetGuid());

        // A preferência nunca cria movimentação: nada de lançamento, transferência ou pagamento de fatura.
        await using var db = Db();
        Assert.Equal((0, 0, 0), (await db.FinancialTransactions.CountAsync(), await db.FinancialTransfers.CountAsync(), await db.FinancialCreditCardInvoicePayments.CountAsync()));
    }
}
