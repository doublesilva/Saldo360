using System.Security.Claims;
using System.Text.Json;
using Microsoft.AspNetCore.Http;
using Saldo360.Api.Authentication;
using Saldo360.Application.CreditCards;
using Saldo360.Application.Users;
using Saldo360.Application.Workspaces;
using Saldo360.Domain.CreditCards;
using Saldo360.Domain.Workspaces;

namespace Saldo360.Tests.Api;

public sealed class AuthenticatedDocumentPasswordProviderTests
{
    [Theory]
    [InlineData(null, 0, 1)]
    [InlineData("123", 1, 2)]
    [InlineData("12345", 1, 3)]
    public async Task OpensOnlyExpectedCpfFormatsAndStopsAtFirstSuccess(string? required, int cpfCalls, int reads)
    {
        var f = new Fixture { RequiredPassword = required };
        var result = await f.Reader.ReadAsync(new MemoryStream([1]), f.Context, null, default);
        Assert.Equal(cpfCalls, f.CpfCalls); Assert.Equal(reads, f.Attempts.Count); Assert.Equal(required is not null, result.Unlocked);
        if (required is not null) Assert.Equal(["123"], f.Attempts[1]);
        if (required == "12345") Assert.Equal(["12345"], f.Attempts[2]);
    }

    [Fact]
    public async Task NormalizesConventionalPunctuationAndNeverUsesHolderIdentity()
    {
        var f = new Fixture { RequiredPassword = "12345", Cpf = "123.456.789-09" };
        await f.Reader.ReadAsync(new MemoryStream([1]), f.Context with { Card = new("Bank", Guid.NewGuid()) }, null, default);
        Assert.Equal(["123"], f.Attempts[1]); Assert.Equal(["12345"], f.Attempts[2]);
        var resolved = await f.Provider.ResolveForDocumentAsync(f.Context, null, default);
        Assert.DoesNotContain("123", JsonSerializer.Serialize(resolved)); Assert.DoesNotContain("123", resolved.ToString());
    }

    [Theory]
    [InlineData(UserCpfStatus.NotRegistered, "12345678909", "CpfNotRegistered")]
    [InlineData(UserCpfStatus.SessionInvalid, "12345678909", "CpfSessionInvalid")]
    [InlineData(UserCpfStatus.AccessDenied, "12345678909", "CpfAccessDenied")]
    [InlineData(UserCpfStatus.Unavailable, "12345678909", "CpfUnavailable")]
    [InlineData(UserCpfStatus.Available, "11111111111", "CpfInvalid")]
    [InlineData(UserCpfStatus.Available, "12345678900", "CpfInvalid")]
    [InlineData(UserCpfStatus.Available, "CPF 12345678909", "CpfInvalid")]
    public async Task MissingAndFailedProfileStatesRemainDistinctAndAllowManual(UserCpfStatus status, string value, string code)
    {
        var f = new Fixture { Status = status, Cpf = value, RequiredPassword = "synthetic-manual-value" };
        var error = await Assert.ThrowsAsync<CreditCardPdfReadException>(() => f.Reader.ReadAsync(new MemoryStream([1]), f.Context, null, default));
        Assert.Equal(CreditCardPdfReadFailure.PasswordRequired, error.Failure); Assert.Equal(code, error.PasswordResolutionCode);
        Assert.Single(f.Attempts);
        var result = await f.Reader.ReadAsync(new MemoryStream([1]), f.Context, "synthetic-manual-value", default);
        Assert.True(result.Unlocked); Assert.Equal(1, f.CpfCalls); // No second CPF query for manual fallback.
        Assert.DoesNotContain(value, JsonSerializer.Serialize(result)); Assert.DoesNotContain("synthetic-manual-value", error.ToString());
    }

    [Fact]
    public async Task WrongManualPasswordNeverFetchesCpfAndAutomaticFailuresStopAfterTwo()
    {
        var f = new Fixture { RequiredPassword = "different" };
        await Assert.ThrowsAsync<CreditCardPdfReadException>(() => f.Reader.ReadAsync(new MemoryStream([1]), f.Context, "wrong-manual", default));
        Assert.Equal(0, f.CpfCalls); Assert.Equal(2, f.Attempts.Count);
        f.Attempts.Clear();
        await Assert.ThrowsAsync<CreditCardPdfReadException>(() => f.Reader.ReadAsync(new MemoryStream([1]), f.Context, null, default));
        Assert.Equal(3, f.Attempts.Count);
    }

    [Theory]
    [InlineData("user")]
    [InlineData("workspace")]
    [InlineData("card")]
    [InlineData("document")]
    [InlineData("anonymous")]
    public async Task RejectsForeignContextBeforeAccessingCpf(string scenario)
    {
        var f = new Fixture { RequiredPassword = "123" };
        var context = f.Context;
        if (scenario == "user") context = context with { UserId = Guid.NewGuid() };
        if (scenario == "workspace") context = context with { WorkspaceId = Guid.NewGuid() };
        if (scenario == "card") context = context with { CardId = Guid.NewGuid() };
        if (scenario == "document") context = context with { DocumentId = Guid.NewGuid() };
        if (scenario == "anonymous") f.Http.HttpContext!.User = new ClaimsPrincipal(new ClaimsIdentity());
        var result = await f.Provider.ResolveForDocumentAsync(context, null, default);
        Assert.Empty(result.Candidates); Assert.Equal("CpfAccessDenied", result.FailureCode); Assert.Equal(0, f.CpfCalls);
    }

    [Fact]
    public async Task AdministrativeReaderNeverQueriesCpfButAcceptsPasswordFileValue()
    {
        var f = new Fixture { RequiredPassword = "12345" };
        await Assert.ThrowsAsync<CreditCardPdfReadException>(() => f.Reader.ReadAsync(new MemoryStream([1]), f.Context, null, default, allowAutomatic: false));
        Assert.Equal(0, f.CpfCalls);
        Assert.True((await f.Reader.ReadAsync(new MemoryStream([1]), f.Context, "12345", default, allowAutomatic: false)).Unlocked);
        Assert.Equal(0, f.CpfCalls);
    }

    private sealed class Fixture : ICurrentUserCpf, IUserDirectory, IFinancialWorkspaceRepository, ICreditCardPdfContextRepository, IFinancialDocumentInboxRepository, ICreditCardPdfTextExtractor
    {
        private readonly Guid user = Guid.NewGuid(), workspace = Guid.NewGuid(), card = Guid.NewGuid(), document = Guid.NewGuid();
        public string? RequiredPassword; public string Cpf = "12345678909"; public UserCpfStatus Status = UserCpfStatus.Available;
        public int CpfCalls; public List<IReadOnlyList<string>> Attempts { get; } = [];
        public HttpContextAccessor Http { get; } = new() { HttpContext = new DefaultHttpContext { User = new ClaimsPrincipal(new ClaimsIdentity([new("iss", "https://auth.synthetic.invalid"), new("sub", "opaque-user")], "synthetic")) } };
        public PdfPasswordContext Context => new(workspace, user, card, document);
        public AuthenticatedDocumentPasswordProvider Provider => new(Http, new CurrentUser(Http, this), this, this, this, this);
        public PdfPasswordReader Reader => new(this, Provider);
        public Task<UserCpfResult> GetAsync(CancellationToken ct) { CpfCalls++; return Task.FromResult(new UserCpfResult(Status, Cpf)); }
        public Task<Guid> ResolveAsync(string issuer, string subject, string? name, CancellationToken ct) => Task.FromResult(user);
        public Task<FinancialWorkspaceRole?> GetOwnerRoleAsync(Guid w, Guid u, CancellationToken ct) => Task.FromResult<FinancialWorkspaceRole?>(w == workspace && u == user ? FinancialWorkspaceRole.Owner : null);
        public Task<FinancialWorkspace> EnsurePersonalAsync(Guid u, DateTimeOffset now, CancellationToken ct) => throw new NotSupportedException();
        public Task<CreditCardPdfContext?> GetAsync(Guid w, Guid c, Guid u, CancellationToken ct) => Task.FromResult<CreditCardPdfContext?>(w == workspace && c == card && u == user ? new("Bank", null) : null);
        Task<FinancialDocumentInboxItem?> IFinancialDocumentInboxRepository.GetAsync(Guid w, Guid d, Guid u, CancellationToken ct) => Task.FromResult(w == workspace && d == document && u == user ? FinancialDocumentInboxItem.Create(document, workspace, FinancialDocumentSource.Manual, null, "synthetic.pdf", new string('a', 64), "application/pdf", "synthetic", DateTimeOffset.UtcNow, DateTimeOffset.UtcNow) : null);
        public Task<bool> CanAccessWorkspaceAsync(Guid w, Guid u, CancellationToken ct) => throw new NotSupportedException();
        public Task<bool> WorkspaceExistsAsync(Guid w, CancellationToken ct) => throw new NotSupportedException();
        public Task<FinancialDocumentInboxItem?> FindByHashAsync(Guid w, string hash, CancellationToken ct) => throw new NotSupportedException();
        public Task<FinancialDocumentInboxItem?> FindByExternalIdAsync(Guid w, FinancialDocumentSource source, string id, CancellationToken ct) => throw new NotSupportedException();
        public Task<IReadOnlyList<FinancialDocumentInboxItem>> ListAsync(Guid w, Guid u, CancellationToken ct) => throw new NotSupportedException();
        public Task<bool> TryAddAsync(FinancialDocumentInboxItem item, CancellationToken ct) => throw new NotSupportedException();
        public Task SaveAsync(CancellationToken ct) => throw new NotSupportedException();
        public Task<CreditCardPdfExtractedDocument> ExtractAsync(Stream pdf, int pages, IReadOnlyList<string> passwords, CancellationToken ct)
        {
            Attempts.Add(passwords.ToArray());
            if (RequiredPassword is not null && !passwords.Contains(RequiredPassword)) throw new CreditCardPdfReadException(passwords.Count == 0 ? CreditCardPdfReadFailure.PasswordRequired : CreditCardPdfReadFailure.PasswordRejected);
            return Task.FromResult(new CreditCardPdfExtractedDocument([new(1, "SYNTHETIC ITEM 10.00")]));
        }
        public CreditCardPdfPreview Parse(CreditCardPdfExtractedDocument document, string file, string? competence, string? date) => throw new NotSupportedException();
    }
}
