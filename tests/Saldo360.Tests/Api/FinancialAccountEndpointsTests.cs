using System.Net;
using System.Text;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Saldo360.Tests.Support;

namespace Saldo360.Tests.Api;

/// <summary>Contas financeiras sobre PostgreSQL real, com dois membros (via convite) e uma família estranha.</summary>
[Collection(PostgresCollection.Name)]
[Trait("Category", "CORE")]
public sealed class FinancialAccountEndpointsTests(PostgresFixture postgres) : PostgresApiTestBase(postgres)
{
    private string _owner = null!;
    private string _ownerSecondSession = null!;
    private string _outsider = null!;
    private WorkspaceDto _family = null!;
    private WorkspaceDto _otherFamily = null!;

    private Guid OwnerMemberId => _family.OwnerUserId;

    private Guid GuestMemberId => _family.OwnerUserId;

    private string AccountsUrl => $"/api/financial-workspaces/{_family.Id}/accounts";

    private async Task ArrangeAsync()
    {
        _owner = await SignInAsync("dona");
        _ownerSecondSession = _owner;
        _outsider = await SignInAsync("estranho");
        var created = await CreateWorkspaceAsync(_owner, "Família Silva");
        _family = created;
        _otherFamily = await CreateWorkspaceAsync(_outsider, "Família Estranha");
    }

    [Fact]
    public async Task Owner_CreatesAccount_WithServerDefinedFields()
    {
        await ArrangeAsync();

        var response = await SendWithCsrfAsync(HttpMethod.Post, AccountsUrl, _owner, new
        {
            name = "  Itaú PF  ",
            institution = "Itaú",
            type = "Checking",
            financialScope = "PF",
            responsibleUserId = OwnerMemberId,
        });

        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        var account = await ReadAsync<AccountDto>(response);
        Assert.Equal($"{AccountsUrl}/{account.Id}", response.Headers.Location!.ToString());
        Assert.Equal(("Itaú PF", "Itaú", "Checking", "PF", true, "BRL"),
            (account.Name, account.Institution, account.Type, account.FinancialScope, account.IsActive, account.Currency));
        Assert.Equal(new ResponsibleDto(OwnerMemberId, "Maria Silva"), account.Responsible);

        await using var db = Db();
        var stored = await db.FinancialAccounts.SingleAsync();
        Assert.Equal(_family.Id, stored.WorkspaceId);
        Assert.Equal(await db.Users.Where(u => u.Subject == "dona").Select(u => u.Id).SingleAsync(), stored.CreatedByUserId);
    }

    [Fact]
    public async Task OwnerSessions_SeeTheSameAccounts_AndCanCreateAndEdit()
    {
        await ArrangeAsync();
        await CreateAccountAsync(_owner, "Itaú PF", "Checking", "PF", OwnerMemberId);
        var created = await CreateAccountAsync(_ownerSecondSession, "Itaú Empresas", "Checking", "PJ", GuestMemberId);
        await CreateAccountAsync(_ownerSecondSession, "Dinheiro", "Cash", "PF", responsible: null);

        var edited = await SendWithCsrfAsync(HttpMethod.Put, $"{AccountsUrl}/{created.Id}", _ownerSecondSession, new
        {
            name = "Itaú Empresas",
            institution = "Itaú",
            type = "Savings",
            financialScope = "PJ",
            responsibleUserId = (Guid?)null,
            isActive = true,
        });
        Assert.Equal(HttpStatusCode.OK, edited.StatusCode);

        var ownerView = await ListAsync(_owner);
        var memberView = await ListAsync(_ownerSecondSession);
        Assert.Equal(["Dinheiro", "Itaú Empresas", "Itaú PF"], ownerView.Select(a => a.Name));
        Assert.Equal(JsonSerializer.Serialize(ownerView), JsonSerializer.Serialize(memberView));
        var empresas = ownerView.Single(a => a.Name == "Itaú Empresas");
        Assert.Equal(("Savings", null), (empresas.Type, empresas.Responsible));
        Assert.Null(ownerView.Single(a => a.Name == "Dinheiro").Responsible); // conjunta
    }

    [Fact]
    public async Task Outsider_CannotReadOrChangeAccounts()
    {
        await ArrangeAsync();
        var account = await CreateAccountAsync(_owner, "Itaú PF", "Checking", "PF", OwnerMemberId);
        var body = new { name = "Invadida", institution = (string?)null, type = "Cash", financialScope = "PF", responsibleUserId = (Guid?)null, isActive = true };

        Assert.Equal(HttpStatusCode.NotFound, (await SendAsync(HttpMethod.Get, AccountsUrl, _outsider)).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await SendAsync(HttpMethod.Get, $"{AccountsUrl}/{account.Id}", _outsider)).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await SendWithCsrfAsync(HttpMethod.Post, AccountsUrl, _outsider, new { name = "Invadida", type = "Cash", financialScope = "PF" })).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await SendWithCsrfAsync(HttpMethod.Put, $"{AccountsUrl}/{account.Id}", _outsider, body)).StatusCode);

        // Pela rota da própria família, a conta alheia também não existe.
        var otherRoute = $"/api/financial-workspaces/{_otherFamily.Id}/accounts/{account.Id}";
        Assert.Equal(HttpStatusCode.NotFound, (await SendAsync(HttpMethod.Get, otherRoute, _outsider)).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await SendWithCsrfAsync(HttpMethod.Put, otherRoute, _outsider, body)).StatusCode);

        await using var db = Db();
        Assert.Equal("Itaú PF", (await db.FinancialAccounts.SingleAsync()).Name);
    }

    [Fact]
    public async Task ResponsibleFromAnotherFamily_Returns400_WithoutRevealingIt()
    {
        await ArrangeAsync();
        var foreignMemberId = _otherFamily.OwnerUserId;

        foreach (var responsible in new[] { foreignMemberId, Guid.CreateVersion7() })
        {
            var response = await SendWithCsrfAsync(HttpMethod.Post, AccountsUrl, _owner, new
            {
                name = "Conta", type = "Checking", financialScope = "PF", responsibleUserId = responsible,
            });

            Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
            var text = await response.Content.ReadAsStringAsync();
            Assert.Contains("Responsável inválido.", text);
            Assert.DoesNotContain("Família Estranha", text);
            Assert.DoesNotContain(foreignMemberId.ToString(), text);
        }

        await using var db = Db();
        Assert.Equal(0, await db.FinancialAccounts.CountAsync());
    }

    [Fact]
    public async Task Update_ToResponsibleFromAnotherFamily_Returns400_AndKeepsAccount()
    {
        await ArrangeAsync();
        var account = await CreateAccountAsync(_owner, "Itaú PF", "Checking", "PF", OwnerMemberId);

        var response = await SendWithCsrfAsync(HttpMethod.Put, $"{AccountsUrl}/{account.Id}", _owner, new
        {
            name = "Itaú PF", type = "Checking", financialScope = "PF",
            responsibleUserId = _otherFamily.OwnerUserId, isActive = true,
        });

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        await using var db = Db();
        Assert.Equal(OwnerMemberId, (await db.FinancialAccounts.SingleAsync()).ResponsibleUserId);
    }

    [Theory]
    [InlineData("Credit", "PF", "type")]
    [InlineData("checking", "PF", "type")]
    [InlineData("1", "PF", "type")]
    [InlineData(null, "PF", "type")]
    [InlineData("Checking", "PFJ", "financialScope")]
    [InlineData("Checking", "pj", "financialScope")]
    [InlineData("Checking", "0", "financialScope")]
    [InlineData("Checking", null, "financialScope")]
    public async Task InvalidTypeOrScope_Returns400(string? type, string? scope, string field)
    {
        await ArrangeAsync();

        var response = await SendWithCsrfAsync(HttpMethod.Post, AccountsUrl, _owner, new { name = "Conta", type, financialScope = scope });

        await AssertValidationErrorAsync(response, field);
    }

    [Theory]
    [InlineData("", "name")]
    [InlineData("   ", "name")]
    [InlineData(null, "name")]
    public async Task MissingName_Returns400(string? name, string field)
    {
        await ArrangeAsync();

        var response = await SendWithCsrfAsync(HttpMethod.Post, AccountsUrl, _owner, new { name, type = "Cash", financialScope = "PF" });

        await AssertValidationErrorAsync(response, field);
    }

    [Fact]
    public async Task TooLongNameOrInstitution_Returns400()
    {
        await ArrangeAsync();

        await AssertValidationErrorAsync(
            await SendWithCsrfAsync(HttpMethod.Post, AccountsUrl, _owner, new { name = new string('a', 101), type = "Cash", financialScope = "PF" }),
            "name");
        await AssertValidationErrorAsync(
            await SendWithCsrfAsync(HttpMethod.Post, AccountsUrl, _owner, new { name = "Conta", institution = new string('b', 101), type = "Cash", financialScope = "PF" }),
            "institution");
        Assert.Equal(HttpStatusCode.Created,
            (await SendWithCsrfAsync(HttpMethod.Post, AccountsUrl, _owner, new { name = new string('a', 100), institution = new string('b', 100), type = "Cash", financialScope = "PF" })).StatusCode);
    }

    [Fact]
    public async Task ClientCannotSetFamilyCreatorOrInitialState()
    {
        await ArrangeAsync();
        var account = await CreateAccountAsync(_owner, "Itaú PF", "Checking", "PF", OwnerMemberId);

        var post = await SendWithCsrfAsync(HttpMethod.Post, AccountsUrl, _owner, new
        {
            name = "Conta", type = "Cash", financialScope = "PF", workspaceId = _otherFamily.Id,
        });
        var postInactive = await SendWithCsrfAsync(HttpMethod.Post, AccountsUrl, _owner, new
        {
            name = "Conta", type = "Cash", financialScope = "PF", isActive = false,
        });
        var put = await SendWithCsrfAsync(HttpMethod.Put, $"{AccountsUrl}/{account.Id}", _owner, new
        {
            name = "Itaú PF", type = "Checking", financialScope = "PF", isActive = true, createdByUserId = Guid.CreateVersion7(),
        });

        Assert.Equal([HttpStatusCode.BadRequest, HttpStatusCode.BadRequest, HttpStatusCode.BadRequest],
            new[] { post.StatusCode, postInactive.StatusCode, put.StatusCode });
        await using var db = Db();
        Assert.Equal(1, await db.FinancialAccounts.CountAsync());
    }

    [Fact]
    public async Task Update_RequiresIsActive()
    {
        await ArrangeAsync();
        var account = await CreateAccountAsync(_owner, "Itaú PF", "Checking", "PF", OwnerMemberId);

        var response = await SendWithCsrfAsync(HttpMethod.Put, $"{AccountsUrl}/{account.Id}", _owner, new
        {
            name = "Itaú PF", type = "Checking", financialScope = "PF",
        });

        await AssertValidationErrorAsync(response, "isActive");
    }

    [Fact]
    public async Task Deactivate_HidesByDefault_PreservesRow_AndReactivates()
    {
        await ArrangeAsync();
        var account = await CreateAccountAsync(_owner, "Itaú PF", "Checking", "PF", OwnerMemberId);
        object Body(bool active) => new { name = "Itaú PF", institution = "Itaú", type = "Checking", financialScope = "PF", responsibleUserId = OwnerMemberId, isActive = active };

        Assert.Equal(HttpStatusCode.OK, (await SendWithCsrfAsync(HttpMethod.Put, $"{AccountsUrl}/{account.Id}", _ownerSecondSession, Body(false))).StatusCode);

        Assert.Empty(await ListAsync(_owner));
        var all = await ListAsync(_owner, includeInactive: true);
        Assert.False(Assert.Single(all).IsActive);
        Assert.Equal(HttpStatusCode.OK, (await SendAsync(HttpMethod.Get, $"{AccountsUrl}/{account.Id}", _owner)).StatusCode);
        await using (var db = Db())
        {
            Assert.Equal(1, await db.FinancialAccounts.CountAsync());
        }

        Assert.Equal(HttpStatusCode.OK, (await SendWithCsrfAsync(HttpMethod.Put, $"{AccountsUrl}/{account.Id}", _owner, Body(true))).StatusCode);
        Assert.True(Assert.Single(await ListAsync(_ownerSecondSession)).IsActive);
    }

    [Fact]
    public async Task WriteOperations_RequireCsrf()
    {
        await ArrangeAsync();
        var account = await CreateAccountAsync(_owner, "Itaú PF", "Checking", "PF", OwnerMemberId);

        var post = await SendJsonAsync(HttpMethod.Post, AccountsUrl, _owner, csrf: null, new { name = "Sem CSRF", type = "Cash", financialScope = "PF" });
        var put = await SendJsonAsync(HttpMethod.Put, $"{AccountsUrl}/{account.Id}", _owner, csrf: null,
            new { name = "Alterada", type = "Cash", financialScope = "PF", isActive = false });

        Assert.Equal(HttpStatusCode.BadRequest, post.StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, put.StatusCode);
        await using var db = Db();
        var stored = await db.FinancialAccounts.SingleAsync();
        Assert.Equal(("Itaú PF", true), (stored.Name, stored.IsActive));
    }

    [Fact]
    public async Task Responses_ExposeOnlyUserIdAndName_ForResponsible()
    {
        await ArrangeAsync();
        await CreateAccountAsync(_owner, "Itaú PF", "Checking", "PF", GuestMemberId);

        var body = await (await SendAsync(HttpMethod.Get, AccountsUrl, _owner)).Content.ReadAsStringAsync();

        using var document = JsonDocument.Parse(body);
        var responsible = document.RootElement[0].GetProperty("responsible");
        Assert.Equal(["userId", "displayName"], responsible.EnumerateObject().Select(p => p.Name));
        foreach (var forbidden in new[] { "issuer", "subject", "memberId", "createdBy", "email", Saldo360ApiFactory.TestAuthority })
        {
            Assert.DoesNotContain(forbidden, body, StringComparison.OrdinalIgnoreCase);
        }
    }

    [Theory]
    [InlineData("1234.56", "1234.56", "1234.56")]
    [InlineData("0", "0", "0.00")]
    [InlineData("0.00", "0.00", "0.00")]
    [InlineData("\"0.10\"", "0.10", "0.10")]
    [InlineData("\"-1234.56\"", "-1234.56", "-1234.56")]
    [InlineData("-1500.75", "-1500.75", "-1500.75")]
    [InlineData("-0.01", "-0.01", "-0.01")]
    [InlineData("9999999999999.99", "9999999999999.99", "9999999999999.99")]
    [InlineData("-9999999999999.99", "-9999999999999.99", "-9999999999999.99")]
    public async Task OpeningBalance_PositiveZeroOrNegative_IsStoredExactly_AndReturnedAsExactString(
        string json, string stored, string returned)
    {
        await ArrangeAsync();

        var response = await SendRawWithCsrfAsync(HttpMethod.Post, AccountsUrl, _owner,
            $$"""{ "name": "Itaú PF", "type": "Checking", "financialScope": "PF", "openingBalance": {{json}}, "openingBalanceDate": "2026-10-01" }""");

        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        var id = await AssertOpeningBalanceJsonAsync(response, returned, "2026-10-01");
        Assert.Equal($"Itaú PF {stored} 2026-10-01", await StoredTextAsync());

        // Consulta, listagem e edição só do nome: mesmo texto, mesma data.
        await AssertOpeningBalanceJsonAsync(await SendAsync(HttpMethod.Get, $"{AccountsUrl}/{id}", _ownerSecondSession), returned, "2026-10-01");
        using (var list = JsonDocument.Parse(await (await SendAsync(HttpMethod.Get, AccountsUrl, _ownerSecondSession)).Content.ReadAsStringAsync()))
        {
            AssertOpeningBalance(list.RootElement[0], returned, "2026-10-01");
        }

        var renamed = await SendRawWithCsrfAsync(HttpMethod.Put, $"{AccountsUrl}/{id}", _ownerSecondSession,
            $$"""{ "name": "Itaú PF renomeada", "type": "Checking", "financialScope": "PF", "openingBalance": "{{returned}}", "openingBalanceDate": "2026-10-01", "isActive": true }""");
        Assert.Equal(HttpStatusCode.OK, renamed.StatusCode);
        await AssertOpeningBalanceJsonAsync(renamed, returned, "2026-10-01");
        // O valor gravado não muda: EF compara decimal por valor (0 == 0.00) e nem reescreve a coluna.
        Assert.Equal($"Itaú PF renomeada {stored} 2026-10-01", await StoredTextAsync());

        async Task<string> StoredTextAsync()
        {
            await using var db = Db();
            return await db.Database.SqlQueryRaw<string>(
                "SELECT name || ' ' || opening_balance::text || ' ' || opening_balance_date::text AS \"Value\" FROM financial_accounts").SingleAsync();
        }
    }

    [Fact]
    public async Task OpeningBalanceNull_IsReturnedAsJsonNull_InEveryResponse_AndRemovalMakesBalanceUnavailable()
    {
        await ArrangeAsync();
        var created = await SendWithCsrfAsync(HttpMethod.Post, AccountsUrl, _owner, new { name = "Dinheiro", type = "Cash", financialScope = "PF" });
        var id = await AssertOpeningBalanceJsonAsync(created, null, null);
        await AssertOpeningBalanceJsonAsync(await SendAsync(HttpMethod.Get, $"{AccountsUrl}/{id}", _owner), null, null);

        var configured = await SendWithCsrfAsync(HttpMethod.Put, $"{AccountsUrl}/{id}", _owner,
            new { name = "Dinheiro", type = "Cash", financialScope = "PF", openingBalance = "-1234.56", openingBalanceDate = "2026-10-01", isActive = true });
        await AssertOpeningBalanceJsonAsync(configured, "-1234.56", "2026-10-01");

        var removed = await SendWithCsrfAsync(HttpMethod.Put, $"{AccountsUrl}/{id}", _owner,
            new { name = "Dinheiro", type = "Cash", financialScope = "PF", openingBalance = (string?)null, openingBalanceDate = (string?)null, isActive = true });
        await AssertOpeningBalanceJsonAsync(removed, null, null);
        using (var list = JsonDocument.Parse(await (await SendAsync(HttpMethod.Get, AccountsUrl, _owner)).Content.ReadAsStringAsync()))
        {
            AssertOpeningBalance(list.RootElement[0], null, null);
        }

        using var balance = JsonDocument.Parse(await (await SendAsync(HttpMethod.Get, $"{AccountsUrl}/{id}/balance", _owner)).Content.ReadAsStringAsync());
        Assert.Equal("OpeningBalanceNotConfigured", balance.RootElement.GetProperty("unavailableReason").GetString());
        Assert.Equal(JsonValueKind.Null, balance.RootElement.GetProperty("balance").ValueKind);
    }


    [Theory]
    [InlineData("\"openingBalance\": 100.00", "openingBalanceDate")]
    [InlineData("\"openingBalance\": 100.00, \"openingBalanceDate\": null", "openingBalanceDate")]
    [InlineData("\"openingBalanceDate\": \"2026-10-01\"", "openingBalance")]
    [InlineData("\"openingBalance\": null, \"openingBalanceDate\": \"2026-10-01\"", "openingBalance")]
    public async Task IncompleteOpeningBalance_Returns400(string fields, string field)
    {
        await ArrangeAsync();

        var response = await SendRawWithCsrfAsync(HttpMethod.Post, AccountsUrl, _owner,
            $$"""{ "name": "Conta", "type": "Cash", "financialScope": "PF", {{fields}} }""");

        await AssertValidationErrorAsync(response, field);
        await using var db = Db();
        Assert.Equal(0, await db.FinancialAccounts.CountAsync());
    }

    [Theory]
    [InlineData("10.005")]
    [InlineData("1.500")]
    [InlineData("-0.001")]
    [InlineData("0.1234567890123456789012345678901")]
    [InlineData("10000000000000")]
    [InlineData("-10000000000000.00")]
    public async Task OpeningBalance_WithMoreThanTwoDecimalsOrOutOfRange_IsRejected_NotRounded(string json)
    {
        await ArrangeAsync();

        var response = await SendRawWithCsrfAsync(HttpMethod.Post, AccountsUrl, _owner,
            $$"""{ "name": "Conta", "type": "Cash", "financialScope": "PF", "openingBalance": {{json}}, "openingBalanceDate": "2026-10-01" }""");

        await AssertValidationErrorAsync(response, "openingBalance");
        await using var db = Db();
        Assert.Equal(0, await db.FinancialAccounts.CountAsync());
    }

    [Theory]
    [InlineData("\"2026-10-01T00:00:00\"")]
    [InlineData("\"2026-10-01T00:00:00Z\"")]
    [InlineData("\"01/10/2026\"")]
    [InlineData("\"2026-02-30\"")]
    public async Task OpeningBalanceDate_WithTimeOrInvalid_IsRejected(string json)
    {
        await ArrangeAsync();

        var response = await SendRawWithCsrfAsync(HttpMethod.Post, AccountsUrl, _owner,
            $$"""{ "name": "Conta", "type": "Cash", "financialScope": "PF", "openingBalance": 10, "openingBalanceDate": {{json}} }""");

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        await using var db = Db();
        Assert.Equal(0, await db.FinancialAccounts.CountAsync());
    }

    [Fact]
    public async Task Update_SetsChangesAndClearsOpeningBalance_PreservingOtherFields()
    {
        await ArrangeAsync();
        var account = await CreateAccountAsync(_owner, "Itaú Empresas", "Checking", "PJ", GuestMemberId);
        var url = $"{AccountsUrl}/{account.Id}";
        object Body(decimal? amount, string? date) => new
        {
            name = "Itaú Empresas", institution = "Itaú", type = "Checking", financialScope = "PJ",
            responsibleUserId = GuestMemberId, openingBalance = amount, openingBalanceDate = date, isActive = true,
        };

        var set = await ReadAsync<AccountDto>(await SendWithCsrfAsync(HttpMethod.Put, url, _ownerSecondSession, Body(-250.40m, "2026-09-30")));
        Assert.Equal(("-250.40", new DateOnly(2026, 9, 30)), (set.OpeningBalance, set.OpeningBalanceDate));
        Assert.Equal(("PJ", "Checking", GuestMemberId), (set.FinancialScope, set.Type, set.Responsible!.UserId));

        var changed = await ReadAsync<AccountDto>(await SendWithCsrfAsync(HttpMethod.Put, url, _owner, Body(0m, "2026-10-01")));
        Assert.Equal(("0.00", new DateOnly(2026, 10, 1)), (changed.OpeningBalance, changed.OpeningBalanceDate));

        // Edição incompleta é recusada e não altera o que estava gravado.
        await AssertValidationErrorAsync(await SendWithCsrfAsync(HttpMethod.Put, url, _owner, Body(10m, null)), "openingBalanceDate");
        await using (var db = Db())
        {
            var stored = await db.FinancialAccounts.SingleAsync();
            Assert.Equal((0m, new DateOnly(2026, 10, 1)), (stored.OpeningBalance, stored.OpeningBalanceDate));
        }

        var cleared = await ReadAsync<AccountDto>(await SendWithCsrfAsync(HttpMethod.Put, url, _owner, Body(null, null)));
        Assert.Equal((null, null), (cleared.OpeningBalance, cleared.OpeningBalanceDate));
    }

    [Fact]
    public async Task OpeningBalance_OfAnotherFamilysAccount_CannotBeReadOrChanged()
    {
        await ArrangeAsync();
        var response = await SendWithCsrfAsync(HttpMethod.Post, AccountsUrl, _owner, new
        {
            name = "Itaú PF", type = "Checking", financialScope = "PF", openingBalance = 500m, openingBalanceDate = "2026-10-01",
        });
        var account = await ReadAsync<AccountDto>(response);

        var put = await SendWithCsrfAsync(HttpMethod.Put, $"/api/financial-workspaces/{_otherFamily.Id}/accounts/{account.Id}", _outsider, new
        {
            name = "Itaú PF", type = "Checking", financialScope = "PF", openingBalance = 0m, openingBalanceDate = "2026-10-01", isActive = true,
        });

        Assert.Equal(HttpStatusCode.NotFound, put.StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await SendAsync(HttpMethod.Get, $"{AccountsUrl}/{account.Id}", _outsider)).StatusCode);
        await using var db = Db();
        Assert.Equal(500m, (await db.FinancialAccounts.SingleAsync()).OpeningBalance);
    }

    [Fact]
    public async Task Endpoints_RequireAuthentication()
    {
        Assert.Equal(HttpStatusCode.Unauthorized, (await Client.GetAsync($"/api/financial-workspaces/{Guid.CreateVersion7()}/accounts")).StatusCode);
    }

    private async Task<AccountDto> CreateAccountAsync(string session, string name, string type, string scope, Guid? responsible)
    {
        var response = await SendWithCsrfAsync(HttpMethod.Post, AccountsUrl, session, new
        {
            name, institution = name.StartsWith("Itaú", StringComparison.Ordinal) ? "Itaú" : null, type, financialScope = scope, responsibleUserId = responsible,
        });
        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        return await ReadAsync<AccountDto>(response);
    }

    private async Task<List<AccountDto>> ListAsync(string session, bool includeInactive = false)
    {
        var response = await SendAsync(HttpMethod.Get, includeInactive ? $"{AccountsUrl}?includeInactive=true" : AccountsUrl, session);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        return await ReadAsync<List<AccountDto>>(response);
    }

    /// <summary>Confere openingBalance (string JSON exata ou null) e a data; devolve o Id da conta.</summary>
    private static async Task<Guid> AssertOpeningBalanceJsonAsync(HttpResponseMessage response, string? amount, string? date)
    {
        Assert.True(response.IsSuccessStatusCode, $"HTTP {(int)response.StatusCode}");
        using var json = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        AssertOpeningBalance(json.RootElement, amount, date);
        return json.RootElement.GetProperty("id").GetGuid();
    }

    private static void AssertOpeningBalance(JsonElement account, string? amount, string? date)
    {
        var value = account.GetProperty("openingBalance");
        var referenceDate = account.GetProperty("openingBalanceDate");
        if (amount is null)
        {
            Assert.Equal((JsonValueKind.Null, JsonValueKind.Null), (value.ValueKind, referenceDate.ValueKind));
            return;
        }

        Assert.Equal((JsonValueKind.String, amount, date), (value.ValueKind, value.GetString(), referenceDate.GetString()));
    }

    private static async Task AssertValidationErrorAsync(HttpResponseMessage response, string field)
    {
        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        using var problem = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        Assert.True(problem.RootElement.GetProperty("errors").TryGetProperty(field, out _), $"esperado erro em '{field}'");
    }

    /// <summary>Envia o JSON exatamente como escrito (para testar números com casas decimais extras).</summary>
    private async Task<HttpResponseMessage> SendRawWithCsrfAsync(HttpMethod method, string url, string session, string json)
    {
        var (csrfCookie, csrfToken) = await GetCsrfAsync(session);
        var request = new HttpRequestMessage(method, url) { Content = new StringContent(json, Encoding.UTF8, "application/json") };
        request.Headers.Add("Cookie", $"{session}; {csrfCookie}");
        request.Headers.Add("X-CSRF-TOKEN", csrfToken);
        return await Client.SendAsync(request);
    }

    private sealed record ResponsibleDto(Guid UserId, string DisplayName);

    private sealed record AccountDto(
        Guid Id, string Name, string? Institution, string Type, string FinancialScope,
        ResponsibleDto? Responsible, string? OpeningBalance, DateOnly? OpeningBalanceDate,
        bool IsActive, string Currency, DateTimeOffset CreatedAt);
}
