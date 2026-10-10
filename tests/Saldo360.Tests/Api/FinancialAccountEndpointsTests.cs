using System.Net;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Saldo360.Tests.Support;

namespace Saldo360.Tests.Api;

/// <summary>Contas financeiras sobre PostgreSQL real, com dois membros (via convite) e uma família estranha.</summary>
[Collection(PostgresCollection.Name)]
public sealed class FinancialAccountEndpointsTests(PostgresFixture postgres) : PostgresApiTestBase(postgres)
{
    private string _owner = null!;
    private string _member = null!;
    private string _outsider = null!;
    private FamilyDto _family = null!;
    private FamilyDto _otherFamily = null!;

    private Guid OwnerMemberId => _family.Members.Single(m => m.Role == "Owner").MemberId;

    private Guid GuestMemberId => _family.Members.Single(m => m.Role == "Member").MemberId;

    private string AccountsUrl => $"/api/families/{_family.Id}/accounts";

    private async Task ArrangeAsync()
    {
        _owner = await SignInAsync("dona");
        _member = await SignInAsync("membro");
        _outsider = await SignInAsync("estranho");
        var created = await CreateFamilyAsync(_owner, "Família Silva");
        _family = await JoinByInvitationAsync(_owner, created.Id, _member);
        _otherFamily = await CreateFamilyAsync(_outsider, "Família Estranha");
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
            responsibleMemberId = OwnerMemberId,
        });

        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        var account = await ReadAsync<AccountDto>(response);
        Assert.Equal($"{AccountsUrl}/{account.Id}", response.Headers.Location!.ToString());
        Assert.Equal(("Itaú PF", "Itaú", "Checking", "PF", true, "BRL"),
            (account.Name, account.Institution, account.Type, account.FinancialScope, account.IsActive, account.Currency));
        Assert.Equal(new ResponsibleDto(OwnerMemberId, "Maria Silva"), account.Responsible);

        await using var db = Db();
        var stored = await db.FinancialAccounts.SingleAsync();
        Assert.Equal(_family.Id, stored.FamilyId);
        Assert.Equal(await db.Users.Where(u => u.Subject == "dona").Select(u => u.Id).SingleAsync(), stored.CreatedByUserId);
    }

    [Fact]
    public async Task BothMembers_SeeTheSameAccounts_AndMemberCanCreateAndEdit()
    {
        await ArrangeAsync();
        await CreateAccountAsync(_owner, "Itaú PF", "Checking", "PF", OwnerMemberId);
        var created = await CreateAccountAsync(_member, "Itaú Empresas", "Checking", "PJ", GuestMemberId);
        await CreateAccountAsync(_member, "Dinheiro", "Cash", "PF", responsible: null);

        var edited = await SendWithCsrfAsync(HttpMethod.Put, $"{AccountsUrl}/{created.Id}", _member, new
        {
            name = "Itaú Empresas",
            institution = "Itaú",
            type = "Savings",
            financialScope = "PJ",
            responsibleMemberId = (Guid?)null,
            isActive = true,
        });
        Assert.Equal(HttpStatusCode.OK, edited.StatusCode);

        var ownerView = await ListAsync(_owner);
        var memberView = await ListAsync(_member);
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
        var body = new { name = "Invadida", institution = (string?)null, type = "Cash", financialScope = "PF", responsibleMemberId = (Guid?)null, isActive = true };

        Assert.Equal(HttpStatusCode.NotFound, (await SendAsync(HttpMethod.Get, AccountsUrl, _outsider)).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await SendAsync(HttpMethod.Get, $"{AccountsUrl}/{account.Id}", _outsider)).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await SendWithCsrfAsync(HttpMethod.Post, AccountsUrl, _outsider, new { name = "Invadida", type = "Cash", financialScope = "PF" })).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await SendWithCsrfAsync(HttpMethod.Put, $"{AccountsUrl}/{account.Id}", _outsider, body)).StatusCode);

        // Pela rota da própria família, a conta alheia também não existe.
        var otherRoute = $"/api/families/{_otherFamily.Id}/accounts/{account.Id}";
        Assert.Equal(HttpStatusCode.NotFound, (await SendAsync(HttpMethod.Get, otherRoute, _outsider)).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await SendWithCsrfAsync(HttpMethod.Put, otherRoute, _outsider, body)).StatusCode);

        await using var db = Db();
        Assert.Equal("Itaú PF", (await db.FinancialAccounts.SingleAsync()).Name);
    }

    [Fact]
    public async Task ResponsibleFromAnotherFamily_Returns400_WithoutRevealingIt()
    {
        await ArrangeAsync();
        var foreignMemberId = _otherFamily.Members.Single().MemberId;

        foreach (var responsible in new[] { foreignMemberId, Guid.CreateVersion7() })
        {
            var response = await SendWithCsrfAsync(HttpMethod.Post, AccountsUrl, _owner, new
            {
                name = "Conta", type = "Checking", financialScope = "PF", responsibleMemberId = responsible,
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
            responsibleMemberId = _otherFamily.Members.Single().MemberId, isActive = true,
        });

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        await using var db = Db();
        Assert.Equal(OwnerMemberId, (await db.FinancialAccounts.SingleAsync()).ResponsibleMemberId);
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
            name = "Conta", type = "Cash", financialScope = "PF", familyId = _otherFamily.Id,
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
        object Body(bool active) => new { name = "Itaú PF", institution = "Itaú", type = "Checking", financialScope = "PF", responsibleMemberId = OwnerMemberId, isActive = active };

        Assert.Equal(HttpStatusCode.OK, (await SendWithCsrfAsync(HttpMethod.Put, $"{AccountsUrl}/{account.Id}", _member, Body(false))).StatusCode);

        Assert.Empty(await ListAsync(_owner));
        var all = await ListAsync(_owner, includeInactive: true);
        Assert.False(Assert.Single(all).IsActive);
        Assert.Equal(HttpStatusCode.OK, (await SendAsync(HttpMethod.Get, $"{AccountsUrl}/{account.Id}", _owner)).StatusCode);
        await using (var db = Db())
        {
            Assert.Equal(1, await db.FinancialAccounts.CountAsync());
        }

        Assert.Equal(HttpStatusCode.OK, (await SendWithCsrfAsync(HttpMethod.Put, $"{AccountsUrl}/{account.Id}", _owner, Body(true))).StatusCode);
        Assert.True(Assert.Single(await ListAsync(_member)).IsActive);
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
    public async Task Responses_ExposeOnlyMemberIdAndName_ForResponsible()
    {
        await ArrangeAsync();
        await CreateAccountAsync(_owner, "Itaú PF", "Checking", "PF", GuestMemberId);

        var body = await (await SendAsync(HttpMethod.Get, AccountsUrl, _owner)).Content.ReadAsStringAsync();

        using var document = JsonDocument.Parse(body);
        var responsible = document.RootElement[0].GetProperty("responsible");
        Assert.Equal(["memberId", "displayName"], responsible.EnumerateObject().Select(p => p.Name));
        foreach (var forbidden in new[] { "issuer", "subject", "userId", "createdBy", "email", Saldo360ApiFactory.TestAuthority })
        {
            Assert.DoesNotContain(forbidden, body, StringComparison.OrdinalIgnoreCase);
        }
    }

    [Fact]
    public async Task Endpoints_RequireAuthentication()
    {
        Assert.Equal(HttpStatusCode.Unauthorized, (await Client.GetAsync($"/api/families/{Guid.CreateVersion7()}/accounts")).StatusCode);
    }

    private async Task<AccountDto> CreateAccountAsync(string session, string name, string type, string scope, Guid? responsible)
    {
        var response = await SendWithCsrfAsync(HttpMethod.Post, AccountsUrl, session, new
        {
            name, institution = name.StartsWith("Itaú", StringComparison.Ordinal) ? "Itaú" : null, type, financialScope = scope, responsibleMemberId = responsible,
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

    private static async Task AssertValidationErrorAsync(HttpResponseMessage response, string field)
    {
        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        using var problem = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        Assert.True(problem.RootElement.GetProperty("errors").TryGetProperty(field, out _), $"esperado erro em '{field}'");
    }

    private sealed record ResponsibleDto(Guid MemberId, string DisplayName);

    private sealed record AccountDto(
        Guid Id, string Name, string? Institution, string Type, string FinancialScope,
        ResponsibleDto? Responsible, bool IsActive, string Currency, DateTimeOffset CreatedAt);
}
