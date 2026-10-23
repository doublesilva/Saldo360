using Saldo360.Domain.CreditCards;

namespace Saldo360.Application.CreditCards;

public sealed class FinancialEmailImportOptions
{
    public bool Enabled { get; set; }
    public string Provider { get; set; } = "Gmail";
    public int LookbackDays { get; set; } = 30;
    public int MaxMessages { get; set; } = 50;
    public string TimeZone { get; set; } = "America/Sao_Paulo";
    public List<string> Schedule { get; set; } = ["09:00", "13:00", "18:00", "23:00"];
    public List<FinancialEmailImportProfile> Sources { get; set; } = [];
    public GmailOptions Gmail { get; set; } = new();
}
public sealed class FinancialEmailImportProfile
{
    public string Name { get; set; } = string.Empty;
    public Guid WorkspaceId { get; set; }
    public string Query { get; set; } = "has:attachment filename:pdf";
    public bool Enabled { get; set; }
    public FinancialEmailCollectionMode CollectionMode { get; set; } = FinancialEmailCollectionMode.Attachments;
    public List<string> AllowedLinkHosts { get; set; } = [];
    public List<string> AllowedLinkPathPrefixes { get; set; } = [];
}
public sealed class GmailOptions
{
    public string Mailbox { get; set; } = string.Empty;
    public string ClientId { get; set; } = string.Empty;
    public string ClientIdFile { get; set; } = string.Empty;
    public string ClientSecret { get; set; } = string.Empty;
    public string ClientSecretFile { get; set; } = string.Empty;
    public string RefreshToken { get; set; } = string.Empty;
    public string RefreshTokenFile { get; set; } = string.Empty;
}

public sealed record FinancialEmailMessage(string Id, DateTimeOffset ReceivedAt);
public sealed record FinancialEmailAttachment(string Id, string PartId, string FileName, string ContentType, long Size);
public interface IFinancialEmailSource
{
    Task<IReadOnlyList<FinancialEmailMessage>> SearchLatestAsync(string query, int limit, CancellationToken cancellationToken)
        => throw new NotSupportedException("Latest search is unavailable.");
    Task<IReadOnlyList<FinancialEmailMessage>> SearchAsync(string query, DateTimeOffset after, int maxMessages, CancellationToken cancellationToken);
    Task<IReadOnlyList<FinancialEmailAttachment>> ListAttachmentsAsync(string messageId, CancellationToken cancellationToken);
    Task<Stream> OpenAttachmentAsync(string messageId, string attachmentId, CancellationToken cancellationToken);
    Task<FinancialEmailBody> GetBodyAsync(string messageId, CancellationToken cancellationToken);
    Task<FinancialEmailBody> GetBodyWithoutAttachmentDownloadsAsync(string messageId, CancellationToken cancellationToken) => GetBodyAsync(messageId, cancellationToken);
}
public interface IFinancialEmailCollectorCheckpointRepository
{
    Task<FinancialEmailCollectorCheckpoint> GetOrCreateAsync(Guid workspaceId, string profileName, CancellationToken cancellationToken);
    Task<IReadOnlyList<FinancialEmailCollectorCheckpoint>> ListAsync(IReadOnlyCollection<Guid> workspaceIds, CancellationToken cancellationToken);
    Task SaveAsync(CancellationToken cancellationToken);
}
public sealed record FinancialEmailCollectorError(string MessageId, string? AttachmentId, string Code);
public sealed record FinancialEmailCollectorResult(string Profile, bool DryRun, string Status, long DurationMilliseconds, int MessagesScanned, int CandidateMessages,
    int AttachmentsScanned, int CandidateAttachments, int AcceptedDocuments, int Duplicates, int NeedsReview, int Failures,
    IReadOnlyList<string> CandidateExternalIds, IReadOnlyList<FinancialEmailCollectorError> Errors)
{
    public string Mode { get; init; } = "Incremental";
    public string? WindowFrom { get; init; }
    public string? WindowThrough { get; init; }
    public string? WindowTimeZone { get; init; }
    public int? MessageLimit { get; init; }
    public bool MessageLimitReached { get; init; }
    public int? ExistingIdentityCandidates { get; init; }
    public int MessagesFound => MessagesScanned;
    public int MetadataInspected => AttachmentsScanned + LinksScanned;
    public int CandidatesFound => CandidateAttachments + CandidateLinks;
    public int AttachmentCandidates => CandidateAttachments;
    public int LinkCandidates => CandidateLinks;
    public int RejectedCandidates { get; init; }
    public int LinksScanned { get; init; }
    public int CandidateLinks { get; init; }
    public int RejectedLinks { get; init; }
    public int LinkedDocumentsDownloaded { get; init; }
}

public interface IFinancialEmailCollector
{
    Task<FinancialEmailCollectorResult?> RunHistoricalAsync(string profileName, Guid userId, bool dryRun, FinancialEmailHistoricalWindow window, CancellationToken cancellationToken)
        => throw new NotSupportedException("Historical scan unavailable.");
    Task<FinancialEmailCollectorResult?> RunAsync(string profileName, Guid userId, bool dryRun, CancellationToken cancellationToken);
    Task<FinancialEmailCollectorResult?> RunScheduledAsync(string profileName, CancellationToken cancellationToken);
}

public sealed class FinancialEmailCollector(
    FinancialEmailImportOptions options,
    IFinancialEmailSource source,
    IFinancialEmailLinkExtractor linkExtractor,
    IFinancialLinkedDocumentDownloader linkedDocumentDownloader,
    IFinancialEmailCollectorCheckpointRepository checkpoints,
    IFinancialDocumentInboxRepository inboxRepository,
    FinancialDocumentInboxService inbox,
    TimeProvider clock) : IFinancialEmailCollector
{
    public async Task<FinancialEmailCollectorResult?> RunAsync(string profileName, Guid userId, bool dryRun, CancellationToken cancellationToken)
    {
        var profile = options.Sources.SingleOrDefault(candidate => candidate.Enabled && string.Equals(candidate.Name, profileName, StringComparison.OrdinalIgnoreCase));
        using var writeScope = Saldo360.Application.Workspaces.FinancialWriteScope.EnterOperation(profile?.WorkspaceId);
        // Enabled controla somente o agendador. A execução manual autenticada permanece
        // disponível para provisionamento, dry-run e recuperação operacional.
        if (!string.Equals(options.Provider, "Gmail", StringComparison.OrdinalIgnoreCase) || profile is null
            || !await inboxRepository.CanAccessWorkspaceAsync(profile.WorkspaceId, userId, cancellationToken)) return null;
        return await RunCoreAsync(profile, dryRun, cancellationToken);
    }

    public async Task<FinancialEmailCollectorResult?> RunHistoricalAsync(string profileName, Guid userId, bool dryRun, FinancialEmailHistoricalWindow window, CancellationToken cancellationToken)
    {
        var profile = options.Sources.SingleOrDefault(p => p.Enabled && string.Equals(p.Name, profileName, StringComparison.OrdinalIgnoreCase));
        using var writeScope = Saldo360.Application.Workspaces.FinancialWriteScope.EnterOperation(profile?.WorkspaceId);
        if (userId == Guid.Empty || !string.Equals(options.Provider, "Gmail", StringComparison.OrdinalIgnoreCase) || profile is null
            || !await inboxRepository.CanAccessWorkspaceAsync(profile.WorkspaceId, userId, cancellationToken)) return null;
        return await RunCoreAsync(profile, dryRun, cancellationToken, window);
    }

    public async Task<FinancialEmailCollectorResult?> RunScheduledAsync(string profileName, CancellationToken cancellationToken)
    {
        var profile = options.Sources.SingleOrDefault(candidate => candidate.Enabled && string.Equals(candidate.Name, profileName, StringComparison.OrdinalIgnoreCase));
        using var writeScope = Saldo360.Application.Workspaces.FinancialWriteScope.EnterOperation(profile?.WorkspaceId);
        if (!options.Enabled || !string.Equals(options.Provider, "Gmail", StringComparison.OrdinalIgnoreCase) || profile is null
            || !await inboxRepository.WorkspaceExistsAsync(profile.WorkspaceId, cancellationToken)) return null;
        return await RunCoreAsync(profile, false, cancellationToken);
    }

    private async Task<FinancialEmailCollectorResult> RunCoreAsync(FinancialEmailImportProfile profile, bool dryRun, CancellationToken cancellationToken, FinancialEmailHistoricalWindow? window = null)
    {
        var checkpoint = window is null ? await checkpoints.GetOrCreateAsync(profile.WorkspaceId, profile.Name, cancellationToken) : null;
        var started = clock.GetUtcNow();
        if (!dryRun && checkpoint is not null) { checkpoint.Start(started); await checkpoints.SaveAsync(cancellationToken); }
        var after = window?.StartUtc.AddSeconds(-1) ?? checkpoint?.LastMessageAt?.AddMinutes(-5) ?? started.AddDays(-Math.Clamp(options.LookbackDays, 1, 90));
        var rawMessages = await source.SearchAsync(window?.BoundQuery(profile.Query) ?? profile.Query, after, window?.MaxMessages ?? Math.Clamp(options.MaxMessages, 1, 500), cancellationToken);
        var messages = window is null ? rawMessages : rawMessages.Where(m => m.ReceivedAt >= window.StartUtc && m.ReceivedAt < window.EndExclusiveUtc).ToList();
        var attachmentsScanned = 0; var candidates = 0; var candidateMessages = 0; var accepted = 0; var duplicates = 0; var review = 0; var failures = 0;
        var linksScanned = 0; var candidateLinks = 0; var rejectedLinks = 0; var linkedDownloads = 0;
        var ids = new List<string>(); var errors = new List<FinancialEmailCollectorError>();
        var existingIdentities = 0; var rejectedAttachments = 0;
        foreach (var message in messages.OrderBy(item => item.ReceivedAt))
        {
            var hasCandidate = false;
            if (profile.CollectionMode is FinancialEmailCollectionMode.Attachments or FinancialEmailCollectionMode.AttachmentsAndLinks)
            {
                IReadOnlyList<FinancialEmailAttachment> attachments;
                try { attachments = await source.ListAttachmentsAsync(message.Id, cancellationToken); }
                catch (Exception exception) when (exception is not OperationCanceledException) { failures++; errors.Add(new(message.Id, null, "AttachmentMetadataFailed")); attachments = []; }
                foreach (var attachment in attachments)
                {
                    attachmentsScanned++;
                    if (!IsPdf(attachment) || attachment.Size <= 0 || attachment.Size > CreditCardPdfPreviewService.MaxFileBytes) { rejectedAttachments++; continue; }
                    hasCandidate = true; candidates++;
                    var externalId = GmailAttachmentIdentity.Create(message.Id, attachment.PartId); ids.Add(externalId);
                    var existing = window is not null && await inboxRepository.FindByExternalIdAsync(profile.WorkspaceId, FinancialDocumentSource.Email, externalId, cancellationToken) is not null;
                    if (existing) existingIdentities++;
                    if (dryRun) continue;
                    if (existing) { duplicates++; continue; }
                    try
                    {
                        await using var downloaded = await source.OpenAttachmentAsync(message.Id, attachment.Id, cancellationToken);
                        using var content = new MemoryStream(); await downloaded.CopyToAsync(content, cancellationToken);
                        if (content.Length is <= 0 or > CreditCardPdfPreviewService.MaxFileBytes || !HasPdfSignature(content)) { failures++; errors.Add(new(message.Id, $"part:{attachment.PartId}", "InvalidPdfContent")); continue; }
                        content.Position = 0;
                        Count(await inbox.ReceiveAutomationAsync(profile.WorkspaceId, content, content.Length, attachment.FileName, attachment.ContentType, externalId, message.ReceivedAt, cancellationToken));
                    }
                    catch (Exception exception) when (exception is not OperationCanceledException) { failures++; errors.Add(new(message.Id, $"part:{attachment.PartId}", "AttachmentProcessingFailed")); }
                }
            }
            if (profile.CollectionMode is FinancialEmailCollectionMode.Links or FinancialEmailCollectionMode.AttachmentsAndLinks)
            {
                try
                {
                    var body = window is not null && dryRun
                        ? await source.GetBodyWithoutAttachmentDownloadsAsync(message.Id, cancellationToken)
                        : await source.GetBodyAsync(message.Id, cancellationToken);
                    var extraction = linkExtractor.Extract(profile, body);
                    linksScanned += extraction.Scanned; rejectedLinks += extraction.Rejected; candidateLinks += extraction.Candidates.Count;
                    foreach (var link in extraction.Candidates)
                    {
                        hasCandidate = true; var externalId = GmailLinkedDocumentIdentity.Create(message.Id, link.Ordinal); ids.Add(externalId);
                        var existing = window is not null && await inboxRepository.FindByExternalIdAsync(profile.WorkspaceId, FinancialDocumentSource.Email, externalId, cancellationToken) is not null;
                        if (existing) existingIdentities++;
                        if (dryRun) continue;
                        if (existing) { duplicates++; continue; }
                        try
                        {
                            var document = await linkedDocumentDownloader.DownloadAsync(profile, link.Url, cancellationToken); linkedDownloads++;
                            using var content = new MemoryStream(document.Content, writable: false);
                            Count(await inbox.ReceiveAutomationAsync(profile.WorkspaceId, content, content.Length, document.FileName, document.ContentType, externalId, message.ReceivedAt, cancellationToken));
                        }
                        catch (Exception exception) when (exception is not OperationCanceledException) { failures++; errors.Add(new(message.Id, $"link:{link.Ordinal}", "LinkedDocumentProcessingFailed")); }
                    }
                }
                catch (Exception exception) when (exception is not OperationCanceledException) { failures++; errors.Add(new(message.Id, null, "MessageBodyProcessingFailed")); }
            }
            if (hasCandidate) candidateMessages++;

            void Count(FinancialDocumentInboxReceiveResult result)
            {
                if (result.Result == "Created") { accepted++; if (result.Item?.Status == "NeedsReview") review++; }
                else if (result.Result == "AlreadyExists") duplicates++;
                else { failures++; errors.Add(new(message.Id, null, result.Result)); }
            }
        }
        if (!dryRun && checkpoint is not null)
        {
            DateTimeOffset? newestSafeMessage = failures > 0 || messages.Count == 0 ? null : messages.Max(item => item.ReceivedAt);
            var completed = clock.GetUtcNow();
            checkpoint.Complete(completed, newestSafeMessage, failures == 0 ? "Success" : "PartialFailure",
                (long)(completed - started).TotalMilliseconds, messages.Count, candidates + candidateLinks, accepted, duplicates, review, failures);
            await checkpoints.SaveAsync(cancellationToken);
        }
        var ended = clock.GetUtcNow();
        var result = new FinancialEmailCollectorResult(profile.Name, dryRun, failures == 0 ? "Success" : "PartialFailure", (long)(ended - started).TotalMilliseconds,
            messages.Count, candidateMessages, attachmentsScanned, candidates, accepted, duplicates, review, failures, ids, errors)
            { LinksScanned = linksScanned, CandidateLinks = candidateLinks, RejectedLinks = rejectedLinks, LinkedDocumentsDownloaded = linkedDownloads,
                RejectedCandidates = rejectedAttachments + rejectedLinks, ExistingIdentityCandidates = window is null ? null : existingIdentities,
                MessageLimitReached = window is not null && rawMessages.Count >= window.MaxMessages };
        return window?.Annotate(result) ?? result;
    }
    private static bool IsPdf(FinancialEmailAttachment attachment) =>
        string.Equals(attachment.ContentType, "application/pdf", StringComparison.OrdinalIgnoreCase)
        || attachment.FileName.EndsWith(".pdf", StringComparison.OrdinalIgnoreCase);
    private static bool HasPdfSignature(MemoryStream content)
    {
        if (content.Length < 5) return false;
        var bytes = content.GetBuffer(); return bytes[0] == (byte)'%' && bytes[1] == (byte)'P' && bytes[2] == (byte)'D' && bytes[3] == (byte)'F' && bytes[4] == (byte)'-';
    }
}

public static class GmailAttachmentIdentity
{
    public static string Create(string messageId, string partId)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(messageId);
        ArgumentException.ThrowIfNullOrWhiteSpace(partId);
        var identity = $"gmail:{messageId}:part:{partId}";
        if (identity.Length >= 300) throw new InvalidOperationException("A identidade externa Gmail excede o limite permitido.");
        return identity;
    }
}
