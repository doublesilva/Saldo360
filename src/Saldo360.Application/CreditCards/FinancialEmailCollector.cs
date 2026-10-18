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
public sealed class FinancialEmailImportProfile { public string Name { get; set; } = string.Empty; public Guid FamilyId { get; set; } public string Query { get; set; } = "has:attachment filename:pdf"; public bool Enabled { get; set; } }
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
public sealed record FinancialEmailAttachment(string Id, string FileName, string ContentType, long Size);
public interface IFinancialEmailSource
{
    Task<IReadOnlyList<FinancialEmailMessage>> SearchAsync(string query, DateTimeOffset after, int maxMessages, CancellationToken cancellationToken);
    Task<IReadOnlyList<FinancialEmailAttachment>> ListAttachmentsAsync(string messageId, CancellationToken cancellationToken);
    Task<Stream> OpenAttachmentAsync(string messageId, string attachmentId, CancellationToken cancellationToken);
}
public interface IFinancialEmailCollectorCheckpointRepository
{
    Task<FinancialEmailCollectorCheckpoint> GetOrCreateAsync(Guid familyId, string profileName, CancellationToken cancellationToken);
    Task<IReadOnlyList<FinancialEmailCollectorCheckpoint>> ListAsync(IReadOnlyCollection<Guid> familyIds, CancellationToken cancellationToken);
    Task SaveAsync(CancellationToken cancellationToken);
}
public sealed record FinancialEmailCollectorError(string MessageId, string? AttachmentId, string Code);
public sealed record FinancialEmailCollectorResult(string Profile, bool DryRun, string Status, long DurationMilliseconds, int MessagesScanned, int CandidateMessages,
    int AttachmentsScanned, int CandidateAttachments, int AcceptedDocuments, int Duplicates, int NeedsReview, int Failures,
    IReadOnlyList<string> CandidateExternalIds, IReadOnlyList<FinancialEmailCollectorError> Errors);

public interface IFinancialEmailCollector
{
    Task<FinancialEmailCollectorResult?> RunAsync(string profileName, Guid userId, bool dryRun, CancellationToken cancellationToken);
    Task<FinancialEmailCollectorResult?> RunScheduledAsync(string profileName, CancellationToken cancellationToken);
}

public sealed class FinancialEmailCollector(
    FinancialEmailImportOptions options,
    IFinancialEmailSource source,
    IFinancialEmailCollectorCheckpointRepository checkpoints,
    IFinancialDocumentInboxRepository inboxRepository,
    FinancialDocumentInboxService inbox,
    TimeProvider clock) : IFinancialEmailCollector
{
    public async Task<FinancialEmailCollectorResult?> RunAsync(string profileName, Guid userId, bool dryRun, CancellationToken cancellationToken)
    {
        var profile = options.Sources.SingleOrDefault(candidate => candidate.Enabled && string.Equals(candidate.Name, profileName, StringComparison.OrdinalIgnoreCase));
        // Enabled controla somente o agendador. A execução manual autenticada permanece
        // disponível para provisionamento, dry-run e recuperação operacional.
        if (!string.Equals(options.Provider, "Gmail", StringComparison.OrdinalIgnoreCase) || profile is null
            || !await inboxRepository.CanAccessFamilyAsync(profile.FamilyId, userId, cancellationToken)) return null;
        return await RunCoreAsync(profile, dryRun, cancellationToken);
    }

    public async Task<FinancialEmailCollectorResult?> RunScheduledAsync(string profileName, CancellationToken cancellationToken)
    {
        var profile = options.Sources.SingleOrDefault(candidate => candidate.Enabled && string.Equals(candidate.Name, profileName, StringComparison.OrdinalIgnoreCase));
        if (!options.Enabled || !string.Equals(options.Provider, "Gmail", StringComparison.OrdinalIgnoreCase) || profile is null
            || !await inboxRepository.FamilyExistsAsync(profile.FamilyId, cancellationToken)) return null;
        return await RunCoreAsync(profile, false, cancellationToken);
    }

    private async Task<FinancialEmailCollectorResult> RunCoreAsync(FinancialEmailImportProfile profile, bool dryRun, CancellationToken cancellationToken)
    {
        var checkpoint = await checkpoints.GetOrCreateAsync(profile.FamilyId, profile.Name, cancellationToken);
        var started = clock.GetUtcNow();
        if (!dryRun) { checkpoint.Start(started); await checkpoints.SaveAsync(cancellationToken); }
        var after = checkpoint.LastMessageAt?.AddMinutes(-5) ?? started.AddDays(-Math.Clamp(options.LookbackDays, 1, 90));
        var messages = await source.SearchAsync(profile.Query, after, Math.Clamp(options.MaxMessages, 1, 500), cancellationToken);
        var attachmentsScanned = 0; var candidates = 0; var candidateMessages = 0; var accepted = 0; var duplicates = 0; var review = 0; var failures = 0;
        var ids = new List<string>(); var errors = new List<FinancialEmailCollectorError>();
        foreach (var message in messages.OrderBy(item => item.ReceivedAt))
        {
            IReadOnlyList<FinancialEmailAttachment> attachments;
            try { attachments = await source.ListAttachmentsAsync(message.Id, cancellationToken); }
            catch (Exception exception) when (exception is not OperationCanceledException) { failures++; errors.Add(new(message.Id, null, "AttachmentMetadataFailed")); continue; }
            var hasCandidate = false;
            foreach (var attachment in attachments)
            {
                attachmentsScanned++;
                if (!IsPdf(attachment) || attachment.Size <= 0 || attachment.Size > CreditCardPdfPreviewService.MaxFileBytes) continue;
                hasCandidate = true; candidates++;
                var externalId = $"gmail:{message.Id}:{attachment.Id}"; ids.Add(externalId);
                if (dryRun) continue;
                try
                {
                    await using var downloaded = await source.OpenAttachmentAsync(message.Id, attachment.Id, cancellationToken);
                    using var content = new MemoryStream(); await downloaded.CopyToAsync(content, cancellationToken);
                    if (content.Length is <= 0 or > CreditCardPdfPreviewService.MaxFileBytes || !HasPdfSignature(content))
                    { failures++; errors.Add(new(message.Id, attachment.Id, "InvalidPdfContent")); continue; }
                    content.Position = 0;
                    var result = await inbox.ReceiveAutomationAsync(profile.FamilyId, content, content.Length, attachment.FileName,
                        attachment.ContentType, externalId, message.ReceivedAt, cancellationToken);
                    if (result.Result == "Created") { accepted++; if (result.Item?.Status == "NeedsReview") review++; }
                    else if (result.Result == "AlreadyExists") duplicates++;
                    else { failures++; errors.Add(new(message.Id, attachment.Id, result.Result)); }
                }
                catch (Exception exception) when (exception is not OperationCanceledException)
                { failures++; errors.Add(new(message.Id, attachment.Id, "AttachmentProcessingFailed")); }
            }
            if (hasCandidate) candidateMessages++;
        }
        if (!dryRun)
        {
            DateTimeOffset? newestSafeMessage = failures > 0 || messages.Count == 0 ? null : messages.Max(item => item.ReceivedAt);
            var completed = clock.GetUtcNow();
            checkpoint.Complete(completed, newestSafeMessage, failures == 0 ? "Success" : "PartialFailure",
                (long)(completed - started).TotalMilliseconds, messages.Count, candidates, accepted, duplicates, review, failures);
            await checkpoints.SaveAsync(cancellationToken);
        }
        var ended = clock.GetUtcNow();
        return new(profile.Name, dryRun, failures == 0 ? "Success" : "PartialFailure", (long)(ended - started).TotalMilliseconds,
            messages.Count, candidateMessages, attachmentsScanned, candidates, accepted, duplicates, review, failures, ids, errors);
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
