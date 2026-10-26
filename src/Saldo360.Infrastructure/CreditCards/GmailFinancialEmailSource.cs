using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text;
using Saldo360.Application.CreditCards;

namespace Saldo360.Infrastructure.CreditCards;

internal sealed class GmailFinancialEmailSource(IHttpClientFactory clients, FinancialEmailImportOptions options) : IFinancialEmailSource
{
    private readonly SemaphoreSlim tokenLock = new(1, 1);
    private string? accessToken;
    private DateTimeOffset expiresAt;

    public async Task<IReadOnlyList<FinancialEmailMessage>> SearchLatestAsync(string query, int limit, CancellationToken cancellationToken)
    {
        if (limit is < 1 or > 500) throw new ArgumentOutOfRangeException(nameof(limit));
        var ids = new HashSet<string>(StringComparer.Ordinal);
        var tokens = new HashSet<string>(StringComparer.Ordinal);
        string? token = null;
        do
        {
            var url = $"gmail/v1/users/{Uri.EscapeDataString(Mailbox())}/messages?q={Uri.EscapeDataString(query)}&maxResults={Math.Min(100, limit)}";
            if (token is not null) url += $"&pageToken={Uri.EscapeDataString(token)}";
            using var response = await SendAsync(HttpMethod.Get, url, EmailFailureStage.GmailSearch, cancellationToken);
            var page = await ReadJsonAsync<GmailMessageList>(response, EmailFailureStage.GmailSearch, cancellationToken);
            if (page.Messages is null) throw new EmailSearchFailureException(EmailFailureStage.GmailSearch, EmailFailureCategory.InvalidResponse);
            foreach (var message in page.Messages)
            {
                if (message is null || string.IsNullOrWhiteSpace(message.Id))
                    throw new EmailSearchFailureException(EmailFailureStage.GmailSearch, EmailFailureCategory.InvalidResponse);
                if (ids.Add(message.Id) && ids.Count > limit) throw new LatestEmailSearchLimitException();
            }
            token = string.IsNullOrEmpty(page.NextPageToken) ? null : page.NextPageToken;
            // Gmail list order is not trusted. Never select from a truncated search.
            if (token is not null && (ids.Count >= limit || !tokens.Add(token) || tokens.Count >= limit)) throw new LatestEmailSearchLimitException();
        } while (token is not null);
        var messages = new List<FinancialEmailMessage>();
        foreach (var id in ids)
        {
            var metadata = await GetMessageAsync(id, cancellationToken);
            messages.Add(new(id, ReceptionDate(metadata.InternalDate)));
        }
        return messages.OrderByDescending(x => x.ReceivedAt).ThenBy(x => x.Id, StringComparer.Ordinal).ToArray();
    }

    public async Task<IReadOnlyList<FinancialEmailMessage>> SearchAsync(string query, DateTimeOffset after, int maxMessages, CancellationToken cancellationToken)
    {
        var q = $"({query}) after:{after.ToUnixTimeSeconds()}";
        var url = $"gmail/v1/users/{Uri.EscapeDataString(Mailbox())}/messages?q={Uri.EscapeDataString(q)}&maxResults={maxMessages}";
        using var response = await SendAsync(HttpMethod.Get, url, EmailFailureStage.GmailSearch, cancellationToken);
        var list = await ReadJsonAsync<GmailMessageList>(response, EmailFailureStage.GmailSearch, cancellationToken);
        if (list.Messages is null) throw new EmailSearchFailureException(EmailFailureStage.GmailSearch, EmailFailureCategory.InvalidResponse);
        var result = new List<FinancialEmailMessage>();
        foreach (var item in list.Messages.Take(maxMessages))
        {
            var metadata = await GetMessageAsync(item.Id, cancellationToken);
            if (long.TryParse(metadata.InternalDate, out var milliseconds)) result.Add(new(item.Id, DateTimeOffset.FromUnixTimeMilliseconds(milliseconds)));
        }
        return result;
    }

    public async Task<IReadOnlyList<FinancialEmailAttachment>> ListAttachmentsAsync(string messageId, CancellationToken cancellationToken)
    {
        var message = await GetMessageAsync(messageId, cancellationToken, EmailFailureStage.GmailAttachments); var result = new List<FinancialEmailAttachment>();
        Visit(message.Payload, result); return result;
    }

    public async Task<FinancialEmailBody> GetBodyAsync(string messageId, CancellationToken cancellationToken)
        => await ReadBodyAsync(messageId, cancellationToken, true);

    public async Task<FinancialEmailBody> GetBodyWithoutAttachmentDownloadsAsync(string messageId, CancellationToken cancellationToken)
        => await ReadBodyAsync(messageId, cancellationToken, false);

    private async Task<FinancialEmailBody> ReadBodyAsync(string messageId, CancellationToken cancellationToken, bool allowAttachmentDownloads)
    {
        var message = await GetMessageAsync(messageId, cancellationToken);
        var html = new List<string>(); var plain = new List<string>();
        await VisitBodiesAsync(messageId, message.Payload, html, plain, cancellationToken, allowAttachmentDownloads);
        return new(html.Count == 0 ? null : string.Join("\n", html), plain.Count == 0 ? null : string.Join("\n", plain));
    }

    public async Task<Stream> OpenAttachmentAsync(string messageId, string attachmentId, CancellationToken cancellationToken)
    {
        var url = $"gmail/v1/users/{Uri.EscapeDataString(Mailbox())}/messages/{Uri.EscapeDataString(messageId)}/attachments/{Uri.EscapeDataString(attachmentId)}";
        using var response = await SendAsync(HttpMethod.Get, url, EmailFailureStage.PdfDownload, cancellationToken);
        var body = await ReadJsonAsync<GmailAttachmentBody>(response, EmailFailureStage.PdfDownload, cancellationToken);
        if (string.IsNullOrWhiteSpace(body.Data)) throw new EmailSearchFailureException(EmailFailureStage.PdfDownload, EmailFailureCategory.InvalidResponse);
        return new MemoryStream(DecodeBase64Url(body.Data, EmailFailureStage.PdfDownload), writable: false);
    }

    private async Task<GmailMessage> GetMessageAsync(string id, CancellationToken cancellationToken, EmailFailureStage stage = EmailFailureStage.GmailMessage)
    {
        var url = $"gmail/v1/users/{Uri.EscapeDataString(Mailbox())}/messages/{Uri.EscapeDataString(id)}?format=full";
        using var response = await SendAsync(HttpMethod.Get, url, stage, cancellationToken);
        return await ReadJsonAsync<GmailMessage>(response, stage, cancellationToken);
    }
    private async Task<HttpResponseMessage> SendAsync(HttpMethod method, string url, EmailFailureStage stage, CancellationToken cancellationToken)
    {
        try
        {
            var client = clients.CreateClient("GmailFinancialEmail");
            using var request = new HttpRequestMessage(method, url); request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", await GetTokenAsync(cancellationToken));
            var response = await client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
            if (response.IsSuccessStatusCode) return response;
            // Do not read an error body or retain an exception carrying the request URI.
            var failure = EmailSearchFailureException.From(new HttpRequestException(null, null, response.StatusCode), stage);
            response.Dispose(); throw failure;
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { throw; }
        catch (Exception error) { throw EmailSearchFailureException.From(error, stage); }
    }
    private async Task<string> GetTokenAsync(CancellationToken cancellationToken)
    {
        if (accessToken is not null && expiresAt > DateTimeOffset.UtcNow.AddMinutes(1)) return accessToken;
        await tokenLock.WaitAsync(cancellationToken);
        try
        {
            if (accessToken is not null && expiresAt > DateTimeOffset.UtcNow.AddMinutes(1)) return accessToken;
            var gmail = options.Gmail;
            var clientId = GmailSecretReader.Read(gmail.ClientIdFile, gmail.ClientId, "ClientId");
            var clientSecret = GmailSecretReader.Read(gmail.ClientSecretFile, gmail.ClientSecret, "ClientSecret");
            var refreshToken = GmailSecretReader.Read(gmail.RefreshTokenFile, gmail.RefreshToken, "RefreshToken");
            var client = clients.CreateClient("GmailFinancialEmailToken");
            using var response = await client.PostAsync("token", new FormUrlEncodedContent(new Dictionary<string, string>
            { ["client_id"] = clientId, ["client_secret"] = clientSecret, ["refresh_token"] = refreshToken, ["grant_type"] = "refresh_token" }), cancellationToken);
            if (!response.IsSuccessStatusCode)
                throw EmailSearchFailureException.From(new HttpRequestException(null, null, response.StatusCode), EmailFailureStage.OAuthRefresh);
            var token = await ReadJsonAsync<GmailTokenResponse>(response, EmailFailureStage.OAuthRefresh, cancellationToken);
            if (string.IsNullOrWhiteSpace(token.AccessToken) || token.ExpiresIn <= 0)
                throw new EmailSearchFailureException(EmailFailureStage.OAuthRefresh, EmailFailureCategory.InvalidResponse);
            accessToken = token.AccessToken; expiresAt = DateTimeOffset.UtcNow.AddSeconds(token.ExpiresIn); return accessToken;
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { throw; }
        catch (Exception error) { throw EmailSearchFailureException.From(error, EmailFailureStage.OAuthRefresh); }
        finally { tokenLock.Release(); }
    }
    private string Mailbox() => string.IsNullOrWhiteSpace(options.Gmail.Mailbox)
        ? throw new EmailSearchFailureException(EmailFailureStage.Configuration, EmailFailureCategory.Missing) : options.Gmail.Mailbox;

    private static DateTimeOffset ReceptionDate(string value)
    {
        if (long.TryParse(value, out var milliseconds))
        {
            try { return DateTimeOffset.FromUnixTimeMilliseconds(milliseconds); }
            catch (ArgumentOutOfRangeException) { }
        }
        throw new EmailSearchFailureException(EmailFailureStage.GmailMessage, EmailFailureCategory.InvalidResponse);
    }

    private static async Task<T> ReadJsonAsync<T>(HttpResponseMessage response, EmailFailureStage stage, CancellationToken ct)
    {
        try
        {
            return await response.Content.ReadFromJsonAsync<T>(cancellationToken: ct)
                ?? throw new EmailSearchFailureException(stage, EmailFailureCategory.InvalidResponse);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
        catch (Exception error) { throw EmailSearchFailureException.From(error, stage); }
    }
    private static void Visit(GmailPart? part, List<FinancialEmailAttachment> result)
    {
        if (part is null) return;
        if (!string.IsNullOrWhiteSpace(part.Body?.AttachmentId)) result.Add(new(part.Body.AttachmentId, part.PartId, part.FileName ?? string.Empty, part.MimeType ?? "application/octet-stream", part.Body.Size));
        foreach (var child in part.Parts ?? []) Visit(child, result);
    }
    private async Task VisitBodiesAsync(string messageId, GmailPart? part, List<string> html, List<string> plain, CancellationToken cancellationToken, bool allowAttachmentDownloads)
    {
        if (part is null) return;
        if (string.Equals(part.MimeType, "text/html", StringComparison.OrdinalIgnoreCase) || string.Equals(part.MimeType, "text/plain", StringComparison.OrdinalIgnoreCase))
        {
            byte[] bytes = [];
            if (!string.IsNullOrWhiteSpace(part.Body?.Data)) bytes = DecodeBase64Url(part.Body.Data);
            else if (!string.IsNullOrWhiteSpace(part.Body?.AttachmentId))
            {
                if (!allowAttachmentDownloads) throw new InvalidDataException("Corpo indisponível sem download de anexo; revisão necessária.");
                await using var stream = await OpenAttachmentAsync(messageId, part.Body.AttachmentId, cancellationToken);
                using var memory = new MemoryStream(); await stream.CopyToAsync(memory, cancellationToken); bytes = memory.ToArray();
            }
            if (bytes.Length > 0)
            {
                var value = Encoding.UTF8.GetString(bytes);
                if (string.Equals(part.MimeType, "text/html", StringComparison.OrdinalIgnoreCase)) html.Add(value); else plain.Add(value);
            }
        }
        foreach (var child in part.Parts ?? []) await VisitBodiesAsync(messageId, child, html, plain, cancellationToken, allowAttachmentDownloads);
    }
    private static byte[] DecodeBase64Url(string value, EmailFailureStage stage = EmailFailureStage.GmailMessage)
    {
        try { var normalized = value.Replace('-', '+').Replace('_', '/'); normalized = normalized.PadRight(normalized.Length + (4 - normalized.Length % 4) % 4, '='); return Convert.FromBase64String(normalized); }
        catch (Exception error) { throw EmailSearchFailureException.From(error, stage); }
    }

    private sealed class GmailMessageList { [JsonPropertyName("messages")] public List<GmailMessageRef> Messages { get; set; } = []; [JsonPropertyName("nextPageToken")] public string? NextPageToken { get; set; } }
    private sealed class GmailMessageRef { [JsonPropertyName("id")] public string Id { get; set; } = string.Empty; }
    private sealed class GmailMessage { [JsonPropertyName("internalDate")] public string InternalDate { get; set; } = string.Empty; [JsonPropertyName("payload")] public GmailPart? Payload { get; set; } }
    private sealed class GmailPart { [JsonPropertyName("partId")] public string PartId { get; set; } = string.Empty; [JsonPropertyName("filename")] public string? FileName { get; set; } [JsonPropertyName("mimeType")] public string? MimeType { get; set; } [JsonPropertyName("body")] public GmailAttachmentBody? Body { get; set; } [JsonPropertyName("parts")] public List<GmailPart>? Parts { get; set; } }
    private sealed class GmailAttachmentBody { [JsonPropertyName("attachmentId")] public string AttachmentId { get; set; } = string.Empty; [JsonPropertyName("size")] public long Size { get; set; } [JsonPropertyName("data")] public string Data { get; set; } = string.Empty; }
    private sealed class GmailTokenResponse { [JsonPropertyName("access_token")] public string AccessToken { get; set; } = string.Empty; [JsonPropertyName("expires_in")] public int ExpiresIn { get; set; } }
}

internal static class GmailSecretReader
{
    public static string Read(string? file, string? configuredValue, string name)
    {
        if (!string.IsNullOrWhiteSpace(file))
        {
            try
            {
                var path = Path.GetFullPath(file);
                if (Directory.Exists(path)) throw new EmailSearchFailureException(EmailFailureStage.Credentials, EmailFailureCategory.Unreadable);
                var value = File.ReadAllText(path).TrimEnd('\r', '\n');
                if (string.IsNullOrWhiteSpace(value)) throw new EmailSearchFailureException(EmailFailureStage.Credentials, EmailFailureCategory.Empty);
                return value;
            }
            catch (EmailSearchFailureException) { throw; }
            catch (Exception error)
            {
                throw new EmailSearchFailureException(EmailFailureStage.Credentials,
                    error is FileNotFoundException or DirectoryNotFoundException ? EmailFailureCategory.FileNotFound : EmailFailureCategory.Unreadable);
            }
        }
        if (!string.IsNullOrWhiteSpace(configuredValue)) return configuredValue;
        throw new EmailSearchFailureException(EmailFailureStage.Credentials, EmailFailureCategory.Missing);
    }
}
