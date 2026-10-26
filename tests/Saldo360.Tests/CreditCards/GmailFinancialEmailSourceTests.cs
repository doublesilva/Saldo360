using System.Net;
using System.Text;
using Saldo360.Application.CreditCards;
using Saldo360.Infrastructure.CreditCards;

namespace Saldo360.Tests.CreditCards;

[Trait("Category", "CORE")]
[Trait("Area", "Documents")]
public sealed class GmailFinancialEmailSourceTests
{
    [Fact]
    public async Task LatestSearchPaginatesAndSortsByInternalDateWithoutAddingDateFilters()
    {
        var gmail = new RecordingHandler(request => request.RequestUri!.AbsolutePath.EndsWith("/messages", StringComparison.Ordinal)
            ? request.RequestUri.Query.Contains("pageToken=next", StringComparison.Ordinal)
                ? "{\"messages\":[{\"id\":\"new\"}]}" : "{\"messages\":[{\"id\":\"old\"}],\"nextPageToken\":\"next\"}"
            : request.RequestUri.AbsolutePath.EndsWith("/new", StringComparison.Ordinal)
                ? "{\"internalDate\":\"2000000\"}" : "{\"internalDate\":\"1000000\"}");
        var source = LatestSource(gmail);
        var result = await source.SearchLatestAsync("configured-query", 200, default);
        Assert.Equal(new[] { "new", "old" }, result.Select(x => x.Id));
        Assert.Equal(DateTimeOffset.FromUnixTimeMilliseconds(2000000), result[0].ReceivedAt);
        Assert.Equal(2, gmail.Requests.Count(r => r.Uri.AbsolutePath.EndsWith("/messages", StringComparison.Ordinal)));
        Assert.All(gmail.Requests, r => { Assert.Equal(HttpMethod.Get, r.Method); Assert.DoesNotContain("after", r.Uri.Query); Assert.DoesNotContain("before", r.Uri.Query); });
    }

    [Theory]
    [InlineData("{\"messages\":[{\"id\":\"one\"}],\"nextPageToken\":\"more\"}")]
    [InlineData("{\"messages\":[{\"id\":\"one\"},{\"id\":\"two\"}]}")]
    public async Task LatestSearchFailsClosedAtLimit(string response)
    {
        var gmail = new RecordingHandler(_ => response);
        await Assert.ThrowsAsync<LatestEmailSearchLimitException>(() => LatestSource(gmail).SearchLatestAsync("q", 1, default));
        Assert.Single(gmail.Requests);
    }

    [Fact]
    public async Task LatestSearchRejectsUnknownReceptionDate()
    {
        var gmail = new RecordingHandler(r => r.RequestUri!.AbsolutePath.EndsWith("/messages", StringComparison.Ordinal)
            ? "{\"messages\":[{\"id\":\"one\"}]}" : "{}");
        var error = await Assert.ThrowsAsync<EmailSearchFailureException>(() => LatestSource(gmail).SearchLatestAsync("q", 10, default));
        Assert.Equal("GmailMessageInvalidResponse", error.FailureCode);
    }

    private static GmailFinancialEmailSource LatestSource(RecordingHandler gmail) => new(new FakeHttpClientFactory(gmail,
        new RecordingHandler(_ => "{\"access_token\":\"synthetic\",\"expires_in\":3600}")),
        new FinancialEmailImportOptions { Gmail = new() { Mailbox = "mailbox@example.invalid", ClientId = "synthetic", ClientSecret = "synthetic", RefreshToken = "synthetic" } });

    [Fact]
    public async Task HistoricalQuerySendsBothEpochBoundsAndDryBodyNeverDownloadsAttachment()
    {
        var gmail = new RecordingHandler(request => request.RequestUri!.AbsolutePath.EndsWith("/messages", StringComparison.Ordinal)
            ? "{\"messages\":[]}"
            : "{\"payload\":{\"mimeType\":\"text/html\",\"body\":{\"attachmentId\":\"must-not-download\",\"size\":100}}}");
        var source = new GmailFinancialEmailSource(new FakeHttpClientFactory(gmail, new RecordingHandler(_ => "{\"access_token\":\"test-token\",\"expires_in\":3600}")),
            new FinancialEmailImportOptions { Gmail = new() { Mailbox = "mailbox@example.invalid", ClientId = "client", ClientSecret = "test", RefreshToken = "test" } });
        var window = FinancialEmailHistoricalWindow.Create("2026-10-01", "2026-10-31");
        await source.SearchAsync(window.BoundQuery("profile-query"), window.StartUtc.AddSeconds(-1), 50, default);
        var query = Uri.UnescapeDataString(gmail.Requests[0].Uri.Query);
        Assert.Contains("before:" + window.EndExclusiveUtc.ToUnixTimeSeconds(), query);
        Assert.Contains("after:" + window.StartUtc.AddSeconds(-1).ToUnixTimeSeconds(), query);
        await Assert.ThrowsAsync<InvalidDataException>(() => source.GetBodyWithoutAttachmentDownloadsAsync("m1", default));
        Assert.DoesNotContain(gmail.Requests, r => r.Uri.AbsolutePath.Contains("/attachments/", StringComparison.Ordinal));
    }

    [Fact]
    public async Task UsesOfficialReadOnlyEndpointsAndDecodesAttachment()
    {
        var gmail = new RecordingHandler(request => request.RequestUri!.AbsolutePath switch
        {
            "/gmail/v1/users/mailbox%40example.invalid/messages" => "{\"messages\":[{\"id\":\"m1\"}]}",
            "/gmail/v1/users/mailbox%40example.invalid/messages/m1" => "{\"internalDate\":\"1792238400000\",\"payload\":{\"parts\":[{\"partId\":\"1.2\",\"filename\":\"bill.pdf\",\"mimeType\":\"application/pdf\",\"body\":{\"attachmentId\":\"a1\",\"size\":3}}]}}",
            "/gmail/v1/users/mailbox%40example.invalid/messages/m1/attachments/a1" => "{\"data\":\"AQID\",\"size\":3}",
            _ => throw new InvalidOperationException(request.RequestUri.ToString()),
        });
        var token = new RecordingHandler(_ => "{\"access_token\":\"test-token\",\"expires_in\":3600}");
        var factory = new FakeHttpClientFactory(gmail, token);
        var options = new FinancialEmailImportOptions { Gmail = new() { Mailbox = "mailbox@example.invalid", ClientId = "client", ClientSecret = "secret", RefreshToken = "refresh" } };
        var source = new GmailFinancialEmailSource(factory, options);

        var messages = await source.SearchAsync("has:attachment filename:pdf", DateTimeOffset.FromUnixTimeSeconds(1), 10, CancellationToken.None);
        var attachments = await source.ListAttachmentsAsync("m1", CancellationToken.None);
        await using var stream = await source.OpenAttachmentAsync("m1", "a1", CancellationToken.None);
        using var output = new MemoryStream(); await stream.CopyToAsync(output);

        Assert.Single(messages); var attachment = Assert.Single(attachments); Assert.Equal("a1", attachment.Id); Assert.Equal("1.2", attachment.PartId); Assert.Equal([1, 2, 3], output.ToArray());
        Assert.All(gmail.Requests, request => Assert.Equal(HttpMethod.Get, request.Method));
        Assert.Contains(gmail.Requests, request => request.Uri.Query.Contains("after%3A1", StringComparison.Ordinal));
        Assert.All(gmail.Requests, request => Assert.Equal("Bearer", request.AuthorizationScheme));
    }

    [Fact]
    public async Task ReadsInlineHtmlAndPlainTextBodies()
    {
        static string Encoded(string value) => Convert.ToBase64String(Encoding.UTF8.GetBytes(value)).TrimEnd('=').Replace('+', '-').Replace('/', '_');
        var payload = $"{{\"internalDate\":\"1792238400000\",\"payload\":{{\"parts\":[{{\"partId\":\"1\",\"mimeType\":\"text/plain\",\"body\":{{\"data\":\"{Encoded("Texto") }\"}}}},{{\"partId\":\"2\",\"mimeType\":\"text/html\",\"body\":{{\"data\":\"{Encoded("<a href=\\\"https://dmae.example/conta.pdf\\\">PDF</a>")}\"}}}}]}}}}";
        var gmail = new RecordingHandler(_ => payload);
        var source = new GmailFinancialEmailSource(new FakeHttpClientFactory(gmail, new RecordingHandler(_ => "{\"access_token\":\"token\",\"expires_in\":3600}")),
            new FinancialEmailImportOptions { Gmail = new() { Mailbox = "mailbox@example.invalid", ClientId = "client", ClientSecret = "secret", RefreshToken = "refresh" } });

        var body = await source.GetBodyAsync("m1", CancellationToken.None);

        Assert.Equal("Texto", body.PlainText);
        Assert.Contains("https://dmae.example/conta.pdf", body.Html, StringComparison.Ordinal);
    }

    [Fact]
    public void SecretFilesTakePrecedenceAndOnlyTrimTrailingLineBreaks()
    {
        var path = Path.GetTempFileName();
        try
        {
            File.WriteAllText(path, " value with spaces \r\n");
            Assert.Equal(" value with spaces ", GmailSecretReader.Read(path, "fallback", "ClientSecret"));
        }
        finally { File.Delete(path); }
    }

    [Fact]
    public void MissingSecretFileFailsWithoutFallingBackToInlineValue()
    {
        var path = Path.Combine(Path.GetTempPath(), $"saldo360-missing-{Guid.NewGuid():N}");
        var error = Assert.Throws<EmailSearchFailureException>(() => GmailSecretReader.Read(path, "fallback", "RefreshToken"));
        Assert.Equal("GmailCredentialsFileNotFound", error.FailureCode);
        Assert.DoesNotContain(path, error.ToString(), StringComparison.Ordinal);
        Assert.DoesNotContain("fallback", error.ToString(), StringComparison.Ordinal);
        Assert.Null(error.InnerException);
    }

    [Theory]
    [InlineData("search", 401, "GmailSearchUnauthorized")]
    [InlineData("search", 403, "GmailSearchForbidden")]
    [InlineData("search", 429, "GmailSearchRateLimited")]
    [InlineData("message", 401, "GmailMessageUnauthorized")]
    [InlineData("message", 403, "GmailMessageForbidden")]
    [InlineData("message", 429, "GmailMessageRateLimited")]
    [InlineData("attachments", 403, "GmailAttachmentsForbidden")]
    [InlineData("download", 401, "EmailPdfDownloadUnauthorized")]
    [InlineData("download", 403, "EmailPdfDownloadForbidden")]
    [InlineData("download", 429, "EmailPdfDownloadRateLimited")]
    [InlineData("oauth", 401, "GmailOAuthUnauthorized")]
    [InlineData("oauth", 403, "GmailOAuthForbidden")]
    [InlineData("oauth", 429, "GmailOAuthRateLimited")]
    [InlineData("oauth", 400, "GmailOAuthUpstream")]
    [InlineData("search", 503, "GmailSearchUpstream")]
    public async Task HttpFailuresRetainStageAndSafeCategoryWithoutResponseOrRetry(string operation, int status, string code)
    {
        var failure = new FailureHandler((HttpStatusCode)status);
        var gmail = operation == "oauth" ? (HttpMessageHandler)new RecordingHandler(_ => throw new InvalidOperationException("Gmail must not be called")) : failure;
        var oauth = operation == "oauth" ? (HttpMessageHandler)failure : new RecordingHandler(_ => "{\"access_token\":\"private-access-token\",\"expires_in\":3600}");
        var source = new GmailFinancialEmailSource(new FakeHttpClientFactory(gmail, oauth), ConfiguredOptions());
        var error = await Assert.ThrowsAsync<EmailSearchFailureException>(async () =>
        {
            if (operation == "message") await source.GetBodyAsync("private-message-id", default);
            else if (operation == "attachments") await source.ListAttachmentsAsync("private-message-id", default);
            else if (operation == "download") await source.OpenAttachmentAsync("private-message-id", "private-attachment", default);
            else await source.SearchLatestAsync("private-gmail-query", 10, default);
        });
        Assert.Equal(code, error.FailureCode); Assert.Equal(code, error.Message);
        Assert.Null(error.InnerException); Assert.Equal(1, failure.Calls); Assert.True(failure.DisposedResponse);
        Assert.DoesNotContain("private-", error.ToString(), StringComparison.Ordinal);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task TimeoutsAreClassifiedAtTheFailingTransport(bool oauth)
    {
        var timeout = new FailureHandler(error: new TaskCanceledException("private-timeout-token-query"));
        var source = new GmailFinancialEmailSource(new FakeHttpClientFactory(oauth ? new RecordingHandler(_ => "{}") : timeout,
            oauth ? timeout : new RecordingHandler(_ => "{\"access_token\":\"synthetic\",\"expires_in\":3600}")), ConfiguredOptions());
        var error = await Assert.ThrowsAsync<EmailSearchFailureException>(() => source.SearchLatestAsync("private-query", 10, default));
        Assert.Equal(oauth ? "GmailOAuthTimeout" : "GmailSearchTimeout", error.FailureCode);
        Assert.Equal(1, timeout.Calls); Assert.DoesNotContain("private-", error.Message);
    }

    [Fact]
    public async Task CallerCancellationIsNotTurnedIntoTimeoutOrRetried()
    {
        using var cts = new CancellationTokenSource();
        var handler = new FailureHandler(error: new TaskCanceledException());
        var source = new GmailFinancialEmailSource(new FakeHttpClientFactory(handler, handler), ConfiguredOptions());
        cts.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => source.SearchLatestAsync("q", 10, cts.Token));
        Assert.Equal(0, handler.Calls);
    }

    [Theory]
    [InlineData("mailbox", "GmailConfigurationMissing")]
    [InlineData("client-id", "GmailCredentialsMissing")]
    [InlineData("client-secret", "GmailCredentialsMissing")]
    [InlineData("refresh-token", "GmailCredentialsMissing")]
    public async Task MissingConfigurationOrCredentialsNeverCallHttp(string missing, string code)
    {
        var options = ConfiguredOptions();
        if (missing == "mailbox") options.Gmail.Mailbox = "";
        if (missing == "client-id") options.Gmail.ClientId = "";
        if (missing == "client-secret") options.Gmail.ClientSecret = "";
        if (missing == "refresh-token") options.Gmail.RefreshToken = "";
        var forbidden = new FailureHandler(error: new InvalidOperationException("HTTP forbidden"));
        var source = new GmailFinancialEmailSource(new FakeHttpClientFactory(forbidden, forbidden), options);
        var error = await Assert.ThrowsAsync<EmailSearchFailureException>(() => source.SearchLatestAsync("private-query", 10, default));
        Assert.Equal(code, error.FailureCode); Assert.Equal(0, forbidden.Calls); Assert.Null(error.InnerException);
    }

    [Theory]
    [InlineData("empty", "GmailCredentialsEmpty")]
    [InlineData("directory", "GmailCredentialsUnreadable")]
    [InlineData("invalid-path", "GmailCredentialsUnreadable")]
    public void InvalidSecretFilesDoNotLeakPathsAndDoNotUseInlineFallback(string scenario, string code)
    {
        var path = Path.GetTempFileName();
        try
        {
            var input = scenario == "directory" ? Path.GetTempPath() : scenario == "invalid-path" ? "private\0secret" : path;
            var error = Assert.Throws<EmailSearchFailureException>(() => GmailSecretReader.Read(input, "private-inline-token", "untrusted-name"));
            Assert.Equal(code, error.Message); Assert.Null(error.InnerException);
            Assert.DoesNotContain(path, error.ToString()); Assert.DoesNotContain("private-inline-token", error.ToString());
            Assert.DoesNotContain("untrusted-name", error.ToString());
        }
        finally { File.Delete(path); }
    }

    [Theory]
    [InlineData("{}")]
    [InlineData("{\"access_token\":\"private-token\",\"expires_in\":0}")]
    [InlineData("not-json-private-token")]
    public async Task InvalidOAuthResponseFailsBeforeGmail(string response)
    {
        var gmail = new FailureHandler(error: new InvalidOperationException("Gmail forbidden"));
        var source = new GmailFinancialEmailSource(new FakeHttpClientFactory(gmail, new RecordingHandler(_ => response)), ConfiguredOptions());
        var error = await Assert.ThrowsAsync<EmailSearchFailureException>(() => source.SearchLatestAsync("q", 10, default));
        Assert.Equal("GmailOAuthInvalidResponse", error.FailureCode); Assert.Equal(0, gmail.Calls);
        Assert.DoesNotContain("private-token", error.ToString());
    }

    [Theory]
    [InlineData("not-json", "GmailSearchInvalidResponse")]
    [InlineData("null", "GmailSearchInvalidResponse")]
    [InlineData("{\"messages\":null}", "GmailSearchInvalidResponse")]
    [InlineData("{\"messages\":[null]}", "GmailSearchInvalidResponse")]
    [InlineData("{\"messages\":[{}]}", "GmailSearchInvalidResponse")]
    public async Task InvalidSearchResponseIsNotAnEmptySuccessfulSearch(string response, string code)
    {
        var error = await Assert.ThrowsAsync<EmailSearchFailureException>(() => LatestSource(new RecordingHandler(_ => response)).SearchLatestAsync("q", 10, default));
        Assert.Equal(code, error.FailureCode);
    }

    [Theory]
    [InlineData("{\"data\":\"%%%private%%%\"}")]
    [InlineData("{}")]
    public async Task InvalidAttachmentBodyIsDiagnosedAsDownload(string body)
    {
        var error = await Assert.ThrowsAsync<EmailSearchFailureException>(() => LatestSource(new RecordingHandler(_ => body)).OpenAttachmentAsync("m", "a", default));
        Assert.Equal("EmailPdfDownloadInvalidResponse", error.FailureCode); Assert.Null(error.InnerException);
    }

    private static FinancialEmailImportOptions ConfiguredOptions() => new() { Gmail = new()
        { Mailbox = "synthetic@example.invalid", ClientId = "synthetic", ClientSecret = "private-client-secret", RefreshToken = "private-refresh-token" } };

    private sealed class FailureHandler(HttpStatusCode status = HttpStatusCode.OK, Exception? error = null) : HttpMessageHandler
    {
        public int Calls;
        public bool DisposedResponse;
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Calls++;
            if (error is not null) throw error;
            return Task.FromResult(new HttpResponseMessage(status) { Content = new ObservedContent(() => DisposedResponse = true) });
        }
        private sealed class ObservedContent(Action disposed) : StringContent("private-response-body token query financial-content")
        {
            protected override void Dispose(bool disposing) { if (disposing) disposed(); base.Dispose(disposing); }
        }
    }

    private sealed class FakeHttpClientFactory(HttpMessageHandler gmail, HttpMessageHandler token) : IHttpClientFactory
    {
        public HttpClient CreateClient(string name) => name == "GmailFinancialEmailToken"
            ? new(token, false) { BaseAddress = new("https://oauth2.googleapis.com/") }
            : new(gmail, false) { BaseAddress = new("https://gmail.googleapis.com/") };
    }
    private sealed class RecordingHandler(Func<HttpRequestMessage, string> response) : HttpMessageHandler
    {
        public List<(HttpMethod Method, Uri Uri, string? AuthorizationScheme)> Requests { get; } = [];
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Requests.Add((request.Method, request.RequestUri!, request.Headers.Authorization?.Scheme));
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(response(request), Encoding.UTF8, "application/json") });
        }
    }
}
