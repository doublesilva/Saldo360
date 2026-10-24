using System.Net;
using System.Text;
using Microsoft.Extensions.Logging.Abstractions;
using Saldo360.Application.CreditCards;
using Saldo360.Infrastructure.CreditCards;

namespace Saldo360.Tests.CreditCards;

public sealed class OpenAiCreditCardStatementAgentProviderTests
{
    [Theory]
    [InlineData(401, "invalid_api_key", "ProviderAuthenticationFailed")]
    [InlineData(403, "", "ProviderPermissionDenied")]
    [InlineData(404, "model_not_found", "ProviderModelUnavailable")]
    [InlineData(429, "insufficient_quota", "ProviderQuotaExceeded")]
    [InlineData(429, "rate_limit_exceeded", "ProviderRateLimited")]
    [InlineData(400, "invalid_request_error", "ProviderInvalidRequest")]
    public async Task ReportsWhitelistedFailureWithoutReturningProviderMessage(int status, string code, string expected)
    {
        var body = System.Text.Json.JsonSerializer.Serialize(new { error = new { code, message = "sensitive document and credential" } });
        var handler = new RecordingHandler(_ => new((HttpStatusCode)status) { Content = new StringContent(body) });
        var provider = Create(handler, new() { Enabled = true, Mode = CreditCardStatementAgentMode.AgentPrimary, ApiKey = "secret" });
        var error = await Assert.ThrowsAsync<CreditCardStatementAgentUnavailableException>(() => provider.CompleteAsync(new("prompt", "private document"), default));
        Assert.Equal(expected, error.FailureCode); Assert.DoesNotContain("sensitive", error.ToString()); Assert.DoesNotContain("secret", error.ToString());
        Assert.Equal(1, handler.Calls);
    }

    [Fact]
    public async Task MissingCredentialFileNeverFallsBackToInlineCredential()
    {
        var handler = new RecordingHandler(_ => throw new InvalidOperationException());
        var provider = Create(handler, new() { Enabled = true, Mode = CreditCardStatementAgentMode.AgentPrimary, ApiKey = "secret", ApiKeyFile = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N")) });
        var error = await Assert.ThrowsAsync<CreditCardStatementAgentUnavailableException>(() => provider.CompleteAsync(new("prompt", "document"), default));
        Assert.Equal("ProviderCredentialMissing", error.FailureCode); Assert.Equal(0, handler.Calls);
    }
    [Fact]
    public async Task SendsCentralPromptAndStrictSchemaAndReturnsStructuredJson()
    {
        var statement = "{\"schemaVersion\":\"1.0\",\"header\":{\"issuer\":\"Banco\",\"cardLastDigits\":null,\"competence\":null,\"closingDate\":null,\"dueDate\":null,\"officialTotal\":\"0.00\"},\"items\":[]}";
        var apiResponse = System.Text.Json.JsonSerializer.Serialize(new { output = new[] { new { content = new[] { new { type = "output_text", text = statement } } } } });
        var handler = new RecordingHandler(_ => new(HttpStatusCode.OK)
        { Content = new StringContent(apiResponse, Encoding.UTF8, "application/json") });
        var provider = Create(handler, new() { Enabled = true, Mode = CreditCardStatementAgentMode.AgentPrimary, ApiKey = "secret", Model = "test-model" });

        var result = await provider.CompleteAsync(new(CreditCardStatementAgentPrompt.System, "--- PAGE 1 ---\ntext"), CancellationToken.None);

        Assert.Equal(statement, result);
        Assert.Contains("json_schema", handler.Body, StringComparison.Ordinal);
        Assert.Contains("strict", handler.Body, StringComparison.Ordinal);
        using var requestJson = System.Text.Json.JsonDocument.Parse(handler.Body);
        Assert.Equal(CreditCardStatementAgentPrompt.System,
            requestJson.RootElement.GetProperty("input")[0].GetProperty("content")[0].GetProperty("text").GetString());
        Assert.Equal("Bearer", handler.AuthorizationScheme);
        Assert.Equal("secret", handler.AuthorizationParameter);
    }

    [Fact]
    public async Task DisabledProviderFailsWithoutHttpCall()
    {
        var handler = new RecordingHandler(_ => throw new InvalidOperationException());
        var provider = Create(handler, new());
        await Assert.ThrowsAsync<CreditCardStatementAgentUnavailableException>(() => provider.CompleteAsync(new("prompt", "document"), CancellationToken.None));
        Assert.Equal(0, handler.Calls);
    }

    [Fact]
    public async Task HttpFailureAndTimeoutAreSanitized()
    {
        var failed = Create(new RecordingHandler(_ => new(HttpStatusCode.BadGateway)), new() { Enabled = true, Mode = CreditCardStatementAgentMode.AgentPrimary, ApiKey = "secret" });
        var httpError = await Assert.ThrowsAsync<CreditCardStatementAgentUnavailableException>(() => failed.CompleteAsync(new("prompt", "document"), CancellationToken.None));
        Assert.DoesNotContain("secret", httpError.ToString(), StringComparison.Ordinal);

        var timeout = Create(new RecordingHandler(_ => throw new TaskCanceledException()), new() { Enabled = true, Mode = CreditCardStatementAgentMode.AgentPrimary, ApiKey = "secret" });
        var timeoutError = await Assert.ThrowsAsync<CreditCardStatementAgentUnavailableException>(() => timeout.CompleteAsync(new("prompt", "document"), CancellationToken.None));
        Assert.Contains("Timeout", timeoutError.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task LlmAgentSendsOnlyDocumentPagesAndConfiguredCompetence()
    {
        var provider = new CapturingProvider();
        var agent = new LlmCreditCardStatementExtractorAgent(provider);
        await agent.ExtractAsync(new("sensitive-name.pdf", new([new(1, "statement text")]), "2026-10"), CancellationToken.None);
        Assert.Contains("PAGE 1", provider.Request!.DocumentText, StringComparison.Ordinal);
        Assert.Contains("statement text", provider.Request.DocumentText, StringComparison.Ordinal);
        Assert.DoesNotContain("sensitive-name.pdf", provider.Request.DocumentText, StringComparison.Ordinal);
        Assert.DoesNotContain("FamilyId", provider.Request.DocumentText, StringComparison.Ordinal);
    }

    private static OpenAiCreditCardStatementAgentProvider Create(RecordingHandler handler, CreditCardStatementAgentOptions options)
    {
        var factory = new Factory(handler);
        return new(factory, options, NullLogger<OpenAiCreditCardStatementAgentProvider>.Instance);
    }
    private sealed class Factory(RecordingHandler handler) : IHttpClientFactory
    {
        public HttpClient CreateClient(string name) => new(handler, false) { BaseAddress = new("https://api.openai.com/v1/") };
    }
    private sealed class RecordingHandler(Func<HttpRequestMessage, HttpResponseMessage> response) : HttpMessageHandler
    {
        public int Calls { get; private set; } public string Body { get; private set; } = string.Empty;
        public string? AuthorizationScheme { get; private set; } public string? AuthorizationParameter { get; private set; }
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Calls++; Body = request.Content is null ? string.Empty : await request.Content.ReadAsStringAsync(cancellationToken);
            AuthorizationScheme = request.Headers.Authorization?.Scheme; AuthorizationParameter = request.Headers.Authorization?.Parameter;
            return response(request);
        }
    }
    private sealed class CapturingProvider : ICreditCardStatementAgentProvider
    {
        public CreditCardStatementAgentProviderRequest? Request { get; private set; }
        public Task<string> CompleteAsync(CreditCardStatementAgentProviderRequest request, CancellationToken cancellationToken)
        { Request = request; return Task.FromResult("{}"); }
    }
}
