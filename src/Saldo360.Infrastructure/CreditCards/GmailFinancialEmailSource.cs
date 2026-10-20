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

    public async Task<IReadOnlyList<FinancialEmailMessage>> SearchAsync(string query, DateTimeOffset after, int maxMessages, CancellationToken cancellationToken)
    {
        var q = $"({query}) after:{after.ToUnixTimeSeconds()}";
        var url = $"gmail/v1/users/{Uri.EscapeDataString(Mailbox())}/messages?q={Uri.EscapeDataString(q)}&maxResults={maxMessages}";
        using var response = await SendAsync(HttpMethod.Get, url, cancellationToken);
        var list = await response.Content.ReadFromJsonAsync<GmailMessageList>(cancellationToken: cancellationToken) ?? new();
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
        var message = await GetMessageAsync(messageId, cancellationToken); var result = new List<FinancialEmailAttachment>();
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
        using var response = await SendAsync(HttpMethod.Get, url, cancellationToken);
        var body = await response.Content.ReadFromJsonAsync<GmailAttachmentBody>(cancellationToken: cancellationToken)
            ?? throw new InvalidDataException("Resposta de anexo vazia.");
        return new MemoryStream(DecodeBase64Url(body.Data), writable: false);
    }

    private async Task<GmailMessage> GetMessageAsync(string id, CancellationToken cancellationToken)
    {
        var url = $"gmail/v1/users/{Uri.EscapeDataString(Mailbox())}/messages/{Uri.EscapeDataString(id)}?format=full";
        using var response = await SendAsync(HttpMethod.Get, url, cancellationToken);
        return await response.Content.ReadFromJsonAsync<GmailMessage>(cancellationToken: cancellationToken) ?? throw new InvalidDataException("Resposta de mensagem vazia.");
    }
    private async Task<HttpResponseMessage> SendAsync(HttpMethod method, string url, CancellationToken cancellationToken)
    {
        var client = clients.CreateClient("GmailFinancialEmail");
        using var request = new HttpRequestMessage(method, url); request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", await GetTokenAsync(cancellationToken));
        var response = await client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken); response.EnsureSuccessStatusCode(); return response;
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
            response.EnsureSuccessStatusCode();
            var token = await response.Content.ReadFromJsonAsync<GmailTokenResponse>(cancellationToken: cancellationToken) ?? throw new InvalidDataException("Resposta OAuth vazia.");
            accessToken = token.AccessToken; expiresAt = DateTimeOffset.UtcNow.AddSeconds(token.ExpiresIn); return accessToken;
        }
        finally { tokenLock.Release(); }
    }
    private string Mailbox() => string.IsNullOrWhiteSpace(options.Gmail.Mailbox) ? throw new InvalidOperationException("Mailbox Gmail não configurada.") : options.Gmail.Mailbox;
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
    private static byte[] DecodeBase64Url(string value) { var normalized = value.Replace('-', '+').Replace('_', '/'); normalized = normalized.PadRight(normalized.Length + (4 - normalized.Length % 4) % 4, '='); return Convert.FromBase64String(normalized); }

    private sealed class GmailMessageList { [JsonPropertyName("messages")] public List<GmailMessageRef> Messages { get; set; } = []; }
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
            var path = Path.GetFullPath(file);
            if (!File.Exists(path)) throw new InvalidOperationException($"Arquivo do segredo Gmail {name} não encontrado: '{path}'.");
            var value = File.ReadAllText(path).TrimEnd('\r', '\n');
            if (string.IsNullOrWhiteSpace(value)) throw new InvalidOperationException($"Arquivo do segredo Gmail {name} está vazio: '{path}'.");
            return value;
        }
        if (!string.IsNullOrWhiteSpace(configuredValue)) return configuredValue;
        throw new InvalidOperationException($"Credencial OAuth Gmail {name} não configurada.");
    }
}
