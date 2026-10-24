using System.Net;
using System.Text;

namespace Saldo360.Tests.Support;

public sealed class FakeProfileGateway : HttpMessageHandler
{
    public int Calls { get; private set; }
    public HttpStatusCode Status { get; set; } = HttpStatusCode.ServiceUnavailable;
    public string Body { get; set; } = "{}";
    public bool ThrowNetwork { get; set; }
    public string? LastBearer { get; private set; }
    protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
    {
        Calls++;
        Assert.Equal("https://gateway.home.doublesilva.com/api/me", request.RequestUri!.AbsoluteUri);
        Assert.Equal(HttpMethod.Get, request.Method); Assert.Equal("Bearer", request.Headers.Authorization?.Scheme);
        LastBearer = request.Headers.Authorization?.Parameter;
        if (ThrowNetwork) throw new HttpRequestException("Synthetic private exception must never escape");
        return Task.FromResult(new HttpResponseMessage(Status) { Content = new StringContent(Body, Encoding.UTF8, "application/json") });
    }
}
