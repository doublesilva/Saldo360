using System.Net;
using System.Text;
using Saldo360.Application.CreditCards;
using Saldo360.Infrastructure.CreditCards;

namespace Saldo360.Tests.CreditCards;

public sealed class GmailFinancialEmailSourceTests
{
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
