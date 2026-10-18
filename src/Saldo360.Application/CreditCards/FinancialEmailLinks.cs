using System.Net;
using System.Net.Sockets;
using System.Text.RegularExpressions;

namespace Saldo360.Application.CreditCards;

public enum FinancialEmailCollectionMode { Attachments, Links, AttachmentsAndLinks }

public sealed record FinancialEmailBody(string? Html, string? PlainText);
public sealed record FinancialEmailLinkCandidate(int Ordinal, Uri Url);
public sealed record FinancialEmailLinkExtraction(IReadOnlyList<FinancialEmailLinkCandidate> Candidates, int Scanned, int Rejected);
public sealed record FinancialLinkedDocument(byte[] Content, string FileName, string ContentType);

public interface IFinancialEmailLinkExtractor
{
    FinancialEmailLinkExtraction Extract(FinancialEmailImportProfile profile, FinancialEmailBody body);
}

public interface IFinancialLinkedDocumentDownloader
{
    Task<FinancialLinkedDocument> DownloadAsync(FinancialEmailImportProfile profile, Uri url, CancellationToken cancellationToken);
}

public interface IFinancialHostAddressResolver
{
    Task<IPAddress[]> ResolveAsync(string host, CancellationToken cancellationToken);
}

public sealed class FinancialEmailLinkExtractor : IFinancialEmailLinkExtractor
{
    private static readonly Regex HtmlLinks = new("<a\\b[^>]*?\\bhref\\s*=\\s*(?:\"(?<u>[^\"]*)\"|'(?<u>[^']*)')", RegexOptions.IgnoreCase | RegexOptions.Compiled);
    private static readonly Regex PlainLinks = new("https://[^\\s<>\"']+", RegexOptions.IgnoreCase | RegexOptions.Compiled);

    public FinancialEmailLinkExtraction Extract(FinancialEmailImportProfile profile, FinancialEmailBody body)
    {
        var raw = new List<string>();
        if (!string.IsNullOrWhiteSpace(body.Html))
            raw.AddRange(HtmlLinks.Matches(body.Html).Select(match => WebUtility.HtmlDecode(match.Groups["u"].Value)));
        if (!string.IsNullOrWhiteSpace(body.PlainText))
            raw.AddRange(PlainLinks.Matches(body.PlainText).Select(match => match.Value.TrimEnd('.', ',', ';', ')', ']')));

        var candidates = new List<FinancialEmailLinkCandidate>();
        var rejected = 0;
        for (var index = 0; index < raw.Count; index++)
        {
            if (!Uri.TryCreate(raw[index], UriKind.Absolute, out var uri) || !FinancialEmailLinkPolicy.IsAllowed(profile, uri)) { rejected++; continue; }
            candidates.Add(new(index + 1, uri));
        }
        return new(candidates, raw.Count, rejected);
    }
}

public static class FinancialEmailLinkPolicy
{
    public static bool IsAllowed(FinancialEmailImportProfile profile, Uri uri)
    {
        if (!uri.IsAbsoluteUri || !string.Equals(uri.Scheme, Uri.UriSchemeHttps, StringComparison.OrdinalIgnoreCase)
            || !string.IsNullOrEmpty(uri.UserInfo) || IPAddress.TryParse(uri.Host, out _) || string.Equals(uri.Host, "localhost", StringComparison.OrdinalIgnoreCase)) return false;
        var host = uri.IdnHost.TrimEnd('.');
        if (!profile.AllowedLinkHosts.Any(value => !value.Contains('*') && string.Equals(value.Trim().TrimEnd('.'), host, StringComparison.OrdinalIgnoreCase))) return false;
        return profile.AllowedLinkPathPrefixes.Count == 0 || profile.AllowedLinkPathPrefixes.Any(prefix =>
            !string.IsNullOrWhiteSpace(prefix) && uri.AbsolutePath.StartsWith(prefix.StartsWith('/') ? prefix : "/" + prefix, StringComparison.Ordinal));
    }
}

public static class GmailLinkedDocumentIdentity
{
    public static string Create(string messageId, int ordinal)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(messageId);
        if (ordinal <= 0) throw new ArgumentOutOfRangeException(nameof(ordinal));
        var identity = $"gmail:{messageId}:link:{ordinal}";
        if (identity.Length >= 300) throw new InvalidOperationException("A identidade externa Gmail excede o limite permitido.");
        return identity;
    }
}
