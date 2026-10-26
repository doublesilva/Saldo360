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
        await Assert.ThrowsAsync<InvalidDataException>(() => LatestSource(gmail).SearchLatestAsync("q", 10, default));
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
        var error = Assert.Throws<InvalidOperationException>(() => GmailSecretReader.Read(path, "fallback", "RefreshToken"));
        Assert.Contains("não encontrado", error.Message, StringComparison.Ordinal);
        Assert.DoesNotContain("fallback", error.Message, StringComparison.Ordinal);
    }

    private sealed class FakeHttpClientFactory(RecordingHandler gmail, RecordingHandler token) : IHttpClientFactory
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
