using System.Net;
using System.Text;
using Microsoft.Extensions.Logging.Abstractions;
using Saldo360.Application.CreditCards;
using Saldo360.Infrastructure.CreditCards;

namespace Saldo360.Tests.CreditCards;

public sealed class OpenAiCreditCardStatementAgentProviderTests
{
    [Theory]
    [InlineData("gpt-6.1-sol", "medium", "high", 32768)]
    [InlineData("gpt-6.1-sol", "high", "high", 65536)]
    [InlineData("gpt-6-astra", "medium", "original", 32768)]
    public async Task LabReasoningModelsUseExplicitBoundedReasoningAndAllTableImages(string model, string effort, string detail, int tokens)
    {
        var body = System.Text.Json.JsonSerializer.Serialize(new { status = "completed", output = new object[] {
            new { type = "reasoning", summary = Array.Empty<object>() },
            new { type = "message", content = new[] { new { type = "output_text", text = CreditCardStatementAgentSchemaTests.Valid } } } } });
        var handler = new RecordingHandler(_ => new(HttpStatusCode.OK) { Content = new StringContent(body) });
        var provider = new OpenAiCreditCardStatementAgentProvider(new Factory(handler), new()
        { Enabled = true, Mode = CreditCardStatementAgentMode.AgentPrimary, ApiKey = "synthetic", Model = model },
            NullLogger<OpenAiCreditCardStatementAgentProvider>.Instance, new(effort, tokens, detail));
        var png = Convert.FromBase64String("iVBORw0KGgoAAAANSUhEUgAAAAEAAAABCAQAAAC1HAwCAAAAC0lEQVR42mP8/x8AAwMCAO+jRZkAAAAASUVORK5CYII=");
        var result = await provider.CompleteAsync(new("scope", "native table text and OCR")
        { PurchasesOnly = true, ImagePages = [new(1, "native") { ImagePng = png }, new(2, "native table") { ImagePng = png }, new(3, "ocr") { ImagePng = png, TextSource = "OCR" }] }, default);
        Assert.Equal(CreditCardStatementAgentSchemaTests.Valid, result);
        using var json = System.Text.Json.JsonDocument.Parse(handler.Body);
        var root = json.RootElement;
        Assert.Equal(model, root.GetProperty("model").GetString()); Assert.False(root.GetProperty("store").GetBoolean());
        Assert.Equal(effort, root.GetProperty("reasoning").GetProperty("effort").GetString());
        Assert.Equal(tokens, root.GetProperty("max_output_tokens").GetInt32());
        Assert.False(root.TryGetProperty("temperature", out _)); Assert.False(root.TryGetProperty("tools", out _));
        var images = root.GetProperty("input")[1].GetProperty("content").EnumerateArray().Where(c => c.GetProperty("type").GetString() == "input_image").ToArray();
        Assert.Equal(3, images.Length); Assert.All(images, image => Assert.Equal(detail, image.GetProperty("detail").GetString()));
        Assert.True(root.GetProperty("text").GetProperty("format").GetProperty("strict").GetBoolean());
        Assert.Equal(1, handler.Calls);
    }

    [Fact]
    public async Task ApiProviderKeepsMiniDefaultWithoutReasoningOrLargerBudget()
    {
        var body = System.Text.Json.JsonSerializer.Serialize(new { output = new[] { new { content = new[] { new { type = "output_text", text = CreditCardStatementAgentSchemaTests.Valid } } } } });
        var handler = new RecordingHandler(_ => new(HttpStatusCode.OK) { Content = new StringContent(body) });
        await Create(handler, new() { Enabled = true, Mode = CreditCardStatementAgentMode.AgentPrimary, ApiKey = "synthetic" }).CompleteAsync(new("scope", "text"), default);
        using var json = System.Text.Json.JsonDocument.Parse(handler.Body);
        Assert.Equal("gpt-4o-mini", json.RootElement.GetProperty("model").GetString());
        Assert.Equal(12000, json.RootElement.GetProperty("max_output_tokens").GetInt32());
        Assert.False(json.RootElement.TryGetProperty("reasoning", out _));
    }

    [Theory]
    [InlineData("gpt-6.1-sol", "none", 32768, "high")]
    [InlineData("gpt-6.1-sol", "medium", 12000, "high")]
    [InlineData("gpt-6.1-sol", "medium", 65537, "high")]
    [InlineData("gpt-6.1-sol", "medium", 32768, "original")]
    [InlineData("gpt-4o-mini", "medium", 32768, "high")]
    public void InvalidOrUnconfirmedLabConfigurationCannotStartPaidWork(string model, string effort, int tokens, string detail)
        => Assert.Throws<ArgumentException>(() => new StatementExtractionLabPipeline("synthetic", model, "synthetic", 1, effort, tokens, detail));

    [Fact]
    public async Task PurchasesOnlyUsesRestrictedEnumsWithoutChangingSharedSchemaShapeOrDefaultContract()
    {
        var body = System.Text.Json.JsonSerializer.Serialize(new { output = new[] { new { content = new[] { new { type = "output_text", text = CreditCardStatementAgentSchemaTests.Valid } } } } });
        var handler = new RecordingHandler(_ => new(HttpStatusCode.OK) { Content = new StringContent(body) });
        var provider = Create(handler, new() { Enabled = true, Mode = CreditCardStatementAgentMode.AgentPrimary, ApiKey = "synthetic" });
        await provider.CompleteAsync(new("scope", "synthetic") { PurchasesOnly = true }, default);
        using var json = System.Text.Json.JsonDocument.Parse(handler.Body);
        var schema = json.RootElement.GetProperty("text").GetProperty("format").GetProperty("schema");
        AssertContract<CreditCardStatementAgentDocument>(schema);
        AssertContract<CreditCardStatementAgentHeader>(schema.GetProperty("properties").GetProperty("header"));
        var item = schema.GetProperty("properties").GetProperty("items").GetProperty("items");
        AssertContract<CreditCardStatementAgentItem>(item);
        var properties = item.GetProperty("properties");
        Assert.Equal(new[] { "Purchase", "Installment" }, properties.GetProperty("type").GetProperty("enum").EnumerateArray().Select(e => e.GetString()));
        Assert.Equal(new[] { "Debit" }, properties.GetProperty("nature").GetProperty("enum").EnumerateArray().Select(e => e.GetString()));
        Assert.Equal(new[] { "CurrentInvoice", "FutureInstallments" }, properties.GetProperty("section").GetProperty("enum").EnumerateArray().Select(e => e.GetString()));
        Assert.Equal("2.0", schema.GetProperty("properties").GetProperty("schemaVersion").GetProperty("enum")[0].GetString());
        await provider.CompleteAsync(new("original", "synthetic"), default);
        using var original = System.Text.Json.JsonDocument.Parse(handler.Body);
        var allowed = original.RootElement.GetProperty("text").GetProperty("format").GetProperty("schema").GetProperty("properties").GetProperty("items").GetProperty("items").GetProperty("properties").GetProperty("type").GetProperty("enum");
        Assert.Contains(allowed.EnumerateArray(), e => e.GetString() == "Payment");
    }
    [Theory]
    [InlineData("max_output_tokens", "ProviderOutputTruncated")]
    [InlineData("content_filter", "ProviderIncompleteResponse")]
    public async Task IncompleteResponseIsRejectedEvenWhenOutputContainsValidJson(string reason, string code)
    {
        var body = System.Text.Json.JsonSerializer.Serialize(new { status = "incomplete", incomplete_details = new { reason },
            output = new[] { new { content = new[] { new { type = "output_text", text = CreditCardStatementAgentSchemaTests.Valid } } } } });
        var handler = new RecordingHandler(_ => new(HttpStatusCode.OK) { Content = new StringContent(body) });
        var provider = Create(handler, new() { Enabled = true, Mode = CreditCardStatementAgentMode.AgentPrimary, ApiKey = "synthetic" });
        var failure = await Assert.ThrowsAsync<CreditCardStatementAgentUnavailableException>(() => provider.CompleteAsync(new("prompt", "document"), default));
        Assert.Equal(code, failure.FailureCode); Assert.Equal(1, handler.Calls);
    }
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
        var statement = CreditCardStatementAgentSchemaTests.Valid;
        var apiResponse = System.Text.Json.JsonSerializer.Serialize(new { output = new[] { new { content = new[] { new { type = "output_text", text = statement } } } } });
        var handler = new RecordingHandler(_ => new(HttpStatusCode.OK)
        { Content = new StringContent(apiResponse, Encoding.UTF8, "application/json") });
        var provider = Create(handler, new() { Enabled = true, Mode = CreditCardStatementAgentMode.AgentPrimary, ApiKey = "secret", Model = "test-model" });

        var png = Convert.FromBase64String("iVBORw0KGgoAAAANSUhEUgAAAAEAAAABCAQAAAC1HAwCAAAAC0lEQVR42mP8/x8AAwMCAO+jRZkAAAAASUVORK5CYII=");
        var result = await provider.CompleteAsync(new(CreditCardStatementAgentPrompt.System, "--- PAGE 1 ---\ntext")
        { ImagePages = [new(1, "text") { ImagePng = png }] }, CancellationToken.None);

        Assert.Equal(statement, result);
        Assert.Contains("json_schema", handler.Body, StringComparison.Ordinal);
        Assert.Contains("strict", handler.Body, StringComparison.Ordinal);
        using var requestJson = System.Text.Json.JsonDocument.Parse(handler.Body);
        var userContent = requestJson.RootElement.GetProperty("input")[1].GetProperty("content");
        Assert.Equal("input_image", userContent[2].GetProperty("type").GetString());
        Assert.Equal("data:image/png;base64," + Convert.ToBase64String(png), userContent[2].GetProperty("image_url").GetString());
        Assert.Contains("PAGE 1", userContent[1].GetProperty("text").GetString());
        Assert.False(requestJson.RootElement.GetProperty("store").GetBoolean());
        var format = requestJson.RootElement.GetProperty("text").GetProperty("format");
        Assert.True(format.GetProperty("strict").GetBoolean());
        var schema = format.GetProperty("schema");
        AssertContract<CreditCardStatementAgentDocument>(schema);
        var header = schema.GetProperty("properties").GetProperty("header");
        AssertContract<CreditCardStatementAgentHeader>(header);
        var items = schema.GetProperty("properties").GetProperty("items");
        Assert.Equal(2000, items.GetProperty("maxItems").GetInt32());
        var item = items.GetProperty("items");
        AssertContract<CreditCardStatementAgentItem>(item);
        Assert.Equal("date", item.GetProperty("properties").GetProperty("transactionDate").GetProperty("format").GetString());
        Assert.Equal("^[0-9]{4}$", header.GetProperty("properties").GetProperty("cardLastDigits").GetProperty("pattern").GetString());
        Assert.True(CreditCardStatementAgentSchema.TryValidate(result, 1, out _, out var schemaError), schemaError);
        // Every value offered by the provider's enum contract is accepted by C#.
        foreach (var field in new[] { "nature", "type", "section", "confidence", "categoryConfidence" })
        foreach (var value in item.GetProperty("properties").GetProperty(field).GetProperty("enum").EnumerateArray())
        {
            var candidate = System.Text.Json.Nodes.JsonNode.Parse(statement)!;
            candidate["items"]![0]![field] = value.ValueKind == System.Text.Json.JsonValueKind.Null ? null : System.Text.Json.Nodes.JsonValue.Create(value.GetString());
            Assert.True(CreditCardStatementAgentSchema.TryValidate(candidate.ToJsonString(), 1, out _, out schemaError), schemaError);
        }
        foreach (var field in new[] { "officialTotal" })
            Assert.Matches(header.GetProperty("properties").GetProperty(field).GetProperty("pattern").GetString()!, "10.00");
        Assert.Equal(CreditCardStatementAgentPrompt.System,
            requestJson.RootElement.GetProperty("input")[0].GetProperty("content")[0].GetProperty("text").GetString());
        Assert.Equal("Bearer", handler.AuthorizationScheme);
        Assert.Equal("secret", handler.AuthorizationParameter);
    }

    private static void AssertContract<T>(System.Text.Json.JsonElement schema)
    {
        Assert.Equal("object", schema.GetProperty("type").GetString());
        Assert.False(schema.GetProperty("additionalProperties").GetBoolean());
        var properties = schema.GetProperty("properties").EnumerateObject().Select(p => p.Name).Order().ToArray();
        Assert.Equal(properties, schema.GetProperty("required").EnumerateArray().Select(p => p.GetString()!).Order().ToArray());
        Assert.Equal(typeof(T).GetProperties().Select(p => System.Text.Json.JsonNamingPolicy.CamelCase.ConvertName(p.Name)).Order().ToArray(), properties);
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
