using System.Net;
using Saldo360.Application.CreditCards;
using Saldo360.Infrastructure.CreditCards;

namespace Saldo360.Tests.CreditCards;

[Trait("Category", "CORE")]
[Trait("Area", "Documents")]
public sealed class FinancialLinkedDocumentDownloaderTests
{
    [Fact]
    public async Task DownloadsPdfWithoutCredentialsAndUsesSafeName()
    {
        var handler = new Handler(_ => new(HttpStatusCode.OK) { Content = new ByteArrayContent([(byte)'%', (byte)'P', (byte)'D', (byte)'F', (byte)'-', 1]) });
        var downloader = new FinancialLinkedDocumentDownloader(new Factory(handler), new Resolver(IPAddress.Parse("203.0.113.10")));

        var result = await downloader.DownloadAsync(Profile(), new Uri("https://dmae.example/contas/documento.pdf?token=secret"), CancellationToken.None);

        Assert.Equal("documento.pdf", result.FileName);
        Assert.Equal("application/pdf", result.ContentType);
        var request = Assert.Single(handler.Requests);
        Assert.Null(request.Authorization);
        Assert.Equal(HttpMethod.Get, request.Method);
    }

    [Fact]
    public async Task RejectsPrivateDnsAndRedirectOutsideAllowlist()
    {
        var privateDownloader = new FinancialLinkedDocumentDownloader(new Factory(new Handler(_ => new(HttpStatusCode.OK))), new Resolver(IPAddress.Loopback));
        await Assert.ThrowsAsync<InvalidDataException>(() => privateDownloader.DownloadAsync(Profile(), new Uri("https://dmae.example/contas/a.pdf"), CancellationToken.None));

        var redirect = new Handler(_ => new(HttpStatusCode.Redirect) { Headers = { Location = new Uri("https://evil.example/a.pdf") } });
        var redirectedDownloader = new FinancialLinkedDocumentDownloader(new Factory(redirect), new Resolver(IPAddress.Parse("203.0.113.10")));
        await Assert.ThrowsAsync<InvalidDataException>(() => redirectedDownloader.DownloadAsync(Profile(), new Uri("https://dmae.example/contas/a.pdf"), CancellationToken.None));
    }

    private static FinancialEmailImportProfile Profile() => new() { Name = "DMAE", AllowedLinkHosts = ["dmae.example"], AllowedLinkPathPrefixes = ["/contas/"] };
    private sealed class Resolver(params IPAddress[] values) : IFinancialHostAddressResolver { public Task<IPAddress[]> ResolveAsync(string host, CancellationToken cancellationToken) => Task.FromResult(values); }
    private sealed class Factory(HttpMessageHandler handler) : IHttpClientFactory { public HttpClient CreateClient(string name) => new(handler, false); }
    private sealed class Handler(Func<HttpRequestMessage, HttpResponseMessage> response) : HttpMessageHandler
    {
        public List<(HttpMethod Method, System.Net.Http.Headers.AuthenticationHeaderValue? Authorization)> Requests { get; } = [];
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Requests.Add((request.Method, request.Headers.Authorization)); return Task.FromResult(response(request));
        }
    }
}
