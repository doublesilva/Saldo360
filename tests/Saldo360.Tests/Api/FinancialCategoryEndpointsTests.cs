using System.Net;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Saldo360.Domain.Categories;
using Saldo360.Tests.Support;

namespace Saldo360.Tests.Api;

/// <summary>Categorias sobre PostgreSQL real, com dois membros (via convite) e uma família estranha.</summary>
[Collection(PostgresSecondaryCollection.Name)]
[Trait("Category", "CORE")]
public sealed class FinancialCategoryEndpointsTests(PostgresFixture postgres) : PostgresApiTestBase(postgres)
{
    private string _owner = null!;
    private string _ownerSecondSession = null!;
    private string _outsider = null!;
    private WorkspaceDto _family = null!;
    private WorkspaceDto _otherFamily = null!;

    private string CategoriesUrl => $"/api/financial-workspaces/{_family.Id}/categories";

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
    public async Task Owner_CreatesCategory_WithTrimmedName_AndServerDefinedFields()
    {
        await ArrangeAsync();

        var response = await SendWithCsrfAsync(HttpMethod.Post, CategoriesUrl, _owner, new { name = "  Mercado  ", type = "Expense" });

        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        var category = await ReadAsync<CategoryDto>(response);
        Assert.Equal($"{CategoriesUrl}/{category.Id}", response.Headers.Location!.ToString());
        Assert.Equal(("Mercado", "Expense", true), (category.Name, category.Type, category.IsActive));
        Assert.Equal(7, category.Id.Version);

        await using var db = Db();
        var stored = await db.FinancialCategories.SingleAsync();
        Assert.Equal(_family.Id, stored.WorkspaceId);
        Assert.Equal(await db.Users.Where(u => u.Subject == "dona").Select(u => u.Id).SingleAsync(), stored.CreatedByUserId);
    }

    [Fact]
    public async Task BothMembers_SeeTheSameCategories_AndMemberCanCreateAndRename()
    {
        await ArrangeAsync();
        await CreateAsync(_owner, "Salário", "Income");
        var created = await CreateAsync(_ownerSecondSession, "Mercdo", "Expense");

        var renamed = await SendWithCsrfAsync(HttpMethod.Put, $"{CategoriesUrl}/{created.Id}", _ownerSecondSession, new { name = "Mercado", isActive = true });
        Assert.Equal(HttpStatusCode.OK, renamed.StatusCode);

        var ownerView = await ListAsync(_owner);
        var memberView = await ListAsync(_ownerSecondSession);
        Assert.Equal([("Salário", "Income"), ("Mercado", "Expense")], ownerView.Select(c => (c.Name, c.Type)));
        Assert.Equal(JsonSerializer.Serialize(ownerView), JsonSerializer.Serialize(memberView));
    }

    [Fact]
    public async Task Deactivate_HidesByDefault_PreservesRow_AndReactivates()
    {
        await ArrangeAsync();
        var category = await CreateAsync(_owner, "Lazer", "Expense");
        var url = $"{CategoriesUrl}/{category.Id}";

        Assert.Equal(HttpStatusCode.OK, (await SendWithCsrfAsync(HttpMethod.Put, url, _ownerSecondSession, new { name = "Lazer", isActive = false })).StatusCode);

        Assert.Empty(await ListAsync(_owner));
        Assert.False(Assert.Single(await ListAsync(_owner, includeInactive: true)).IsActive);
        Assert.Equal(HttpStatusCode.OK, (await SendAsync(HttpMethod.Get, url, _owner)).StatusCode);

        Assert.Equal(HttpStatusCode.OK, (await SendWithCsrfAsync(HttpMethod.Put, url, _owner, new { name = "Lazer", isActive = true })).StatusCode);
        Assert.True(Assert.Single(await ListAsync(_ownerSecondSession)).IsActive);
        await using var db = Db();
        Assert.Equal(1, await db.FinancialCategories.CountAsync());
    }

    [Theory]
    [InlineData("Mercado")]
    [InlineData("MERCADO")]
    [InlineData("  mercado ")]
    public async Task DuplicateName_InSameFamilyAndType_Returns409_IgnoringCase(string duplicate)
    {
        await ArrangeAsync();
        await CreateAsync(_owner, "Mercado", "Expense");

        var response = await SendWithCsrfAsync(HttpMethod.Post, CategoriesUrl, _ownerSecondSession, new { name = duplicate, type = "Expense" });

        Assert.Equal(HttpStatusCode.Conflict, response.StatusCode);
        Assert.Contains("Já existe uma categoria", await response.Content.ReadAsStringAsync());
        await using var db = Db();
        Assert.Equal(1, await db.FinancialCategories.CountAsync());
    }

    [Fact]
    public async Task DuplicateOfInactiveCategory_Returns409()
    {
        await ArrangeAsync();
        var category = await CreateAsync(_owner, "Academia", "Expense");
        await SendWithCsrfAsync(HttpMethod.Put, $"{CategoriesUrl}/{category.Id}", _owner, new { name = "Academia", isActive = false });

        var response = await SendWithCsrfAsync(HttpMethod.Post, CategoriesUrl, _owner, new { name = "academia", type = "Expense" });

        Assert.Equal(HttpStatusCode.Conflict, response.StatusCode);
    }

    [Fact]
    public async Task SameName_IsAllowed_InOtherTypeOrOtherFamily()
    {
        await ArrangeAsync();
        await CreateAsync(_owner, "Outros", "Expense");

        Assert.Equal(HttpStatusCode.Created,
            (await SendWithCsrfAsync(HttpMethod.Post, CategoriesUrl, _owner, new { name = "Outros", type = "Income" })).StatusCode);
        Assert.Equal(HttpStatusCode.Created,
            (await SendWithCsrfAsync(HttpMethod.Post, $"/api/financial-workspaces/{_otherFamily.Id}/categories", _outsider, new { name = "Outros", type = "Expense" })).StatusCode);
    }

    [Fact]
    public async Task RenameToExistingName_Returns409_AndKeepsName_ButCaseOnlyChangeOfItselfIsAllowed()
    {
        await ArrangeAsync();
        await CreateAsync(_owner, "Mercado", "Expense");
        var feira = await CreateAsync(_owner, "Feira", "Expense");
        var url = $"{CategoriesUrl}/{feira.Id}";

        Assert.Equal(HttpStatusCode.Conflict,
            (await SendWithCsrfAsync(HttpMethod.Put, url, _owner, new { name = "mercado", isActive = true })).StatusCode);
        await using (var db = Db())
        {
            Assert.Equal("Feira", (await db.FinancialCategories.SingleAsync(c => c.Id == feira.Id)).Name);
        }

        var recased = await SendWithCsrfAsync(HttpMethod.Put, url, _owner, new { name = "FEIRA", isActive = true });
        Assert.Equal(HttpStatusCode.OK, recased.StatusCode);
        Assert.Equal("FEIRA", (await ReadAsync<CategoryDto>(recased)).Name);
    }

    [Fact]
    public async Task ConcurrentCreations_WithSameName_CreateExactlyOneCategory()
    {
        await ArrangeAsync();
        var variants = new[] { "Farmácia", "farmácia", "FARMÁCIA", " Farmácia", "Farmácia ", "fArMáCiA", "Farmácia", "farmácia" };

        var responses = await Task.WhenAll(variants.Select((name, i) =>
            SendWithCsrfAsync(HttpMethod.Post, CategoriesUrl, i % 2 == 0 ? _owner : _ownerSecondSession, new { name, type = "Expense" })));

        Assert.Single(responses, r => r.StatusCode == HttpStatusCode.Created);
        Assert.All(responses.Where(r => r.StatusCode != HttpStatusCode.Created), r => Assert.Equal(HttpStatusCode.Conflict, r.StatusCode));
        await using var db = Db();
        Assert.Equal(1, await db.FinancialCategories.CountAsync());
    }

    [Fact]
    public async Task Type_IsImmutable_PutWithTypeIsRejected()
    {
        await ArrangeAsync();
        var category = await CreateAsync(_owner, "Freelance", "Income");

        var response = await SendWithCsrfAsync(HttpMethod.Put, $"{CategoriesUrl}/{category.Id}", _owner,
            new { name = "Freelance", type = "Expense", isActive = true });

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        await using var db = Db();
        Assert.Equal(CategoryType.Income, (await db.FinancialCategories.SingleAsync()).Type);
    }

    [Fact]
    public async Task Outsider_CannotReadOrChangeCategories()
    {
        await ArrangeAsync();
        var category = await CreateAsync(_owner, "Mercado", "Expense");
        var body = new { name = "Invadida", isActive = false };

        Assert.Equal(HttpStatusCode.NotFound, (await SendAsync(HttpMethod.Get, CategoriesUrl, _outsider)).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await SendAsync(HttpMethod.Get, $"{CategoriesUrl}/{category.Id}", _outsider)).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await SendWithCsrfAsync(HttpMethod.Post, CategoriesUrl, _outsider, new { name = "Invadida", type = "Expense" })).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await SendWithCsrfAsync(HttpMethod.Put, $"{CategoriesUrl}/{category.Id}", _outsider, body)).StatusCode);

        // Pela rota da própria família, a categoria alheia também não existe.
        var otherRoute = $"/api/financial-workspaces/{_otherFamily.Id}/categories/{category.Id}";
        Assert.Equal(HttpStatusCode.NotFound, (await SendAsync(HttpMethod.Get, otherRoute, _outsider)).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await SendWithCsrfAsync(HttpMethod.Put, otherRoute, _outsider, body)).StatusCode);

        await using var db = Db();
        var stored = await db.FinancialCategories.SingleAsync();
        Assert.Equal(("Mercado", true), (stored.Name, stored.IsActive));
    }

    [Theory]
    [InlineData("Saving")]
    [InlineData("expense")]
    [InlineData("1")]
    [InlineData(null)]
    public async Task InvalidType_Returns400(string? type)
    {
        await ArrangeAsync();

        await AssertValidationErrorAsync(
            await SendWithCsrfAsync(HttpMethod.Post, CategoriesUrl, _owner, new { name = "Mercado", type }), "type");
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData(null)]
    public async Task MissingName_Returns400_OnCreateAndUpdate(string? name)
    {
        await ArrangeAsync();
        var category = await CreateAsync(_owner, "Mercado", "Expense");

        await AssertValidationErrorAsync(
            await SendWithCsrfAsync(HttpMethod.Post, CategoriesUrl, _owner, new { name, type = "Expense" }), "name");
        await AssertValidationErrorAsync(
            await SendWithCsrfAsync(HttpMethod.Put, $"{CategoriesUrl}/{category.Id}", _owner, new { name, isActive = true }), "name");
    }

    [Fact]
    public async Task NameLength_Limit100()
    {
        await ArrangeAsync();

        await AssertValidationErrorAsync(
            await SendWithCsrfAsync(HttpMethod.Post, CategoriesUrl, _owner, new { name = new string('a', 101), type = "Expense" }), "name");
        Assert.Equal(HttpStatusCode.Created,
            (await SendWithCsrfAsync(HttpMethod.Post, CategoriesUrl, _owner, new { name = new string('a', 100), type = "Expense" })).StatusCode);
    }

    [Fact]
    public async Task Update_RequiresIsActive()
    {
        await ArrangeAsync();
        var category = await CreateAsync(_owner, "Mercado", "Expense");

        await AssertValidationErrorAsync(
            await SendWithCsrfAsync(HttpMethod.Put, $"{CategoriesUrl}/{category.Id}", _owner, new { name = "Mercado" }), "isActive");
    }

    [Fact]
    public async Task ClientCannotSetServerControlledFields()
    {
        await ArrangeAsync();
        var category = await CreateAsync(_owner, "Mercado", "Expense");

        var responses = new[]
        {
            await SendWithCsrfAsync(HttpMethod.Post, CategoriesUrl, _owner, new { name = "A", type = "Expense", workspaceId = _otherFamily.Id }),
            await SendWithCsrfAsync(HttpMethod.Post, CategoriesUrl, _owner, new { name = "B", type = "Expense", isActive = false }),
            await SendWithCsrfAsync(HttpMethod.Post, CategoriesUrl, _owner, new { name = "C", type = "Expense", id = Guid.CreateVersion7() }),
            await SendWithCsrfAsync(HttpMethod.Post, CategoriesUrl, _owner, new { name = "D", type = "Expense", createdAt = DateTimeOffset.UtcNow }),
            await SendWithCsrfAsync(HttpMethod.Put, $"{CategoriesUrl}/{category.Id}", _owner, new { name = "E", isActive = true, createdByUserId = Guid.CreateVersion7() }),
            await SendWithCsrfAsync(HttpMethod.Put, $"{CategoriesUrl}/{category.Id}", _owner, new { name = "F", isActive = true, workspaceId = _otherFamily.Id }),
        };

        Assert.All(responses, r => Assert.Equal(HttpStatusCode.BadRequest, r.StatusCode));
        await using var db = Db();
        Assert.Equal("Mercado", (await db.FinancialCategories.SingleAsync()).Name);
    }

    [Fact]
    public async Task WriteOperations_RequireCsrf()
    {
        await ArrangeAsync();
        var category = await CreateAsync(_owner, "Mercado", "Expense");

        var post = await SendJsonAsync(HttpMethod.Post, CategoriesUrl, _owner, csrf: null, new { name = "Sem CSRF", type = "Expense" });
        var put = await SendJsonAsync(HttpMethod.Put, $"{CategoriesUrl}/{category.Id}", _owner, csrf: null, new { name = "Alterada", isActive = false });

        Assert.Equal(HttpStatusCode.BadRequest, post.StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, put.StatusCode);
        await using var db = Db();
        var stored = await db.FinancialCategories.SingleAsync();
        Assert.Equal(("Mercado", true), (stored.Name, stored.IsActive));
    }

    [Fact]
    public async Task Responses_DoNotExposeCreatorOrFamily()
    {
        await ArrangeAsync();
        await CreateAsync(_owner, "Mercado", "Expense");

        var body = await (await SendAsync(HttpMethod.Get, CategoriesUrl, _owner)).Content.ReadAsStringAsync();

        using var document = JsonDocument.Parse(body);
        Assert.Equal(["id", "name", "type", "isActive", "createdAt"], document.RootElement[0].EnumerateObject().Select(p => p.Name));
    }

    [Fact]
    public async Task Endpoints_RequireAuthentication()
    {
        Assert.Equal(HttpStatusCode.Unauthorized, (await Client.GetAsync($"/api/financial-workspaces/{Guid.CreateVersion7()}/categories")).StatusCode);
    }

    private async Task<CategoryDto> CreateAsync(string session, string name, string type)
    {
        var response = await SendWithCsrfAsync(HttpMethod.Post, CategoriesUrl, session, new { name, type });
        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        return await ReadAsync<CategoryDto>(response);
    }

    private async Task<List<CategoryDto>> ListAsync(string session, bool includeInactive = false)
    {
        var response = await SendAsync(HttpMethod.Get, includeInactive ? $"{CategoriesUrl}?includeInactive=true" : CategoriesUrl, session);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        return await ReadAsync<List<CategoryDto>>(response);
    }

    private static async Task AssertValidationErrorAsync(HttpResponseMessage response, string field)
    {
        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        using var problem = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        Assert.True(problem.RootElement.GetProperty("errors").TryGetProperty(field, out _), $"esperado erro em '{field}'");
    }

    private sealed record CategoryDto(Guid Id, string Name, string Type, bool IsActive, DateTimeOffset CreatedAt);
}
