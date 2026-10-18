using System.Net;
using System.Net.Http.Headers;
using Saldo360.Application.CreditCards;

namespace Saldo360.Infrastructure.CreditCards;

internal sealed class FinancialHostAddressResolver : IFinancialHostAddressResolver
{
    public Task<IPAddress[]> ResolveAsync(string host, CancellationToken cancellationToken) => Dns.GetHostAddressesAsync(host, cancellationToken);
}

internal sealed class FinancialLinkedDocumentDownloader(IHttpClientFactory clients, IFinancialHostAddressResolver addresses) : IFinancialLinkedDocumentDownloader
{
    private const int MaxRedirects = 3;

    public async Task<FinancialLinkedDocument> DownloadAsync(FinancialEmailImportProfile profile, Uri url, CancellationToken cancellationToken)
    {
        var current = url;
        for (var redirect = 0; redirect <= MaxRedirects; redirect++)
        {
            await ValidateAsync(profile, current, cancellationToken);
            using var request = new HttpRequestMessage(HttpMethod.Get, current);
            request.Headers.UserAgent.ParseAdd("Saldo360-FinancialDocumentCollector/1.0");
            using var response = await clients.CreateClient("FinancialLinkedDocument").SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
            if (IsRedirect(response.StatusCode))
            {
                if (redirect == MaxRedirects || response.Headers.Location is null) throw new InvalidDataException("RedirectRejected");
                current = response.Headers.Location.IsAbsoluteUri ? response.Headers.Location : new Uri(current, response.Headers.Location);
                continue;
            }
            response.EnsureSuccessStatusCode();
            if (response.Content.Headers.ContentLength is > CreditCardPdfPreviewService.MaxFileBytes) throw new InvalidDataException("LinkedDocumentTooLarge");
            await using var input = await response.Content.ReadAsStreamAsync(cancellationToken);
            using var output = new MemoryStream(); var buffer = new byte[81920];
            while (true)
            {
                var read = await input.ReadAsync(buffer, cancellationToken); if (read == 0) break;
                if (output.Length + read > CreditCardPdfPreviewService.MaxFileBytes) throw new InvalidDataException("LinkedDocumentTooLarge");
                output.Write(buffer, 0, read);
            }
            var content = output.ToArray();
            if (content.Length < 5 || content[0] != '%' || content[1] != 'P' || content[2] != 'D' || content[3] != 'F' || content[4] != '-') throw new InvalidDataException("InvalidPdfContent");
            return new(content, FileName(response.Content.Headers.ContentDisposition, current, profile.Name), "application/pdf");
        }
        throw new InvalidDataException("RedirectRejected");
    }

    private async Task ValidateAsync(FinancialEmailImportProfile profile, Uri uri, CancellationToken cancellationToken)
    {
        if (!FinancialEmailLinkPolicy.IsAllowed(profile, uri)) throw new InvalidDataException("LinkNotAllowed");
        var resolved = await addresses.ResolveAsync(uri.IdnHost, cancellationToken);
        if (resolved.Length == 0 || resolved.Any(IsPrivate)) throw new InvalidDataException("UnsafeLinkAddress");
    }

    private static bool IsRedirect(HttpStatusCode status) => status is HttpStatusCode.MovedPermanently or HttpStatusCode.Redirect or HttpStatusCode.RedirectMethod or HttpStatusCode.TemporaryRedirect or HttpStatusCode.PermanentRedirect;
    private static bool IsPrivate(IPAddress address)
    {
        if (IPAddress.IsLoopback(address) || address.Equals(IPAddress.Any) || address.Equals(IPAddress.IPv6Any) || address.IsIPv6LinkLocal || address.IsIPv6SiteLocal || address.IsIPv6Multicast) return true;
        if (address.AddressFamily == System.Net.Sockets.AddressFamily.InterNetworkV6 && !address.IsIPv4MappedToIPv6)
        {
            var ipv6 = address.GetAddressBytes();
            return (ipv6[0] & 0xfe) == 0xfc;
        }
        var bytes = address.MapToIPv4().GetAddressBytes();
        return bytes[0] is 0 or 10 or 127 || (bytes[0] == 100 && bytes[1] is >= 64 and <= 127) || (bytes[0] == 169 && bytes[1] == 254)
            || (bytes[0] == 172 && bytes[1] is >= 16 and <= 31) || (bytes[0] == 192 && bytes[1] == 168) || bytes[0] >= 224;
    }
    private static string FileName(ContentDispositionHeaderValue? disposition, Uri uri, string profile)
    {
        var candidate = disposition?.FileNameStar ?? disposition?.FileName ?? Path.GetFileName(uri.AbsolutePath);
        candidate = candidate?.Trim('"');
        if (string.IsNullOrWhiteSpace(candidate) || !candidate.EndsWith(".pdf", StringComparison.OrdinalIgnoreCase)) candidate = $"{profile}-documento.pdf";
        candidate = Path.GetFileName(candidate);
        foreach (var value in Path.GetInvalidFileNameChars()) candidate = candidate.Replace(value, '_');
        return candidate.Length > 120 ? candidate[..116] + ".pdf" : candidate;
    }
}
