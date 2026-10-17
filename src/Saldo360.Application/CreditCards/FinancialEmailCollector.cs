using Saldo360.Domain.CreditCards;

namespace Saldo360.Application.CreditCards;

public sealed class FinancialEmailImportOptions
{
    public bool Enabled { get; set; }
    public string Provider { get; set; } = "Gmail";
    public int LookbackDays { get; set; } = 30;
    public int MaxMessages { get; set; } = 50;
    public List<FinancialEmailImportProfile> Sources { get; set; } = [];
    public GmailOptions Gmail { get; set; } = new();
}
public sealed class FinancialEmailImportProfile { public string Name { get; set; } = string.Empty; public Guid FamilyId { get; set; } public string Query { get; set; } = "has:attachment filename:pdf"; public bool Enabled { get; set; } }
public sealed class GmailOptions { public string Mailbox { get; set; } = string.Empty; public string ClientId { get; set; } = string.Empty; public string ClientSecret { get; set; } = string.Empty; public string RefreshToken { get; set; } = string.Empty; }

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
    Task SaveAsync(CancellationToken cancellationToken);
}
public sealed record FinancialEmailCollectorError(string MessageId, string? AttachmentId, string Code);
public sealed record FinancialEmailCollectorResult(string Profile, bool DryRun, int MessagesScanned, int CandidateMessages,
    int AttachmentsScanned, int CandidateAttachments, int AcceptedDocuments, int Duplicates, int NeedsReview, int Failures,
    IReadOnlyList<string> CandidateExternalIds, IReadOnlyList<FinancialEmailCollectorError> Errors);

public sealed class FinancialEmailCollector(
    FinancialEmailImportOptions options,
    IFinancialEmailSource source,
    IFinancialEmailCollectorCheckpointRepository checkpoints,
    IFinancialDocumentInboxRepository inboxRepository,
    FinancialDocumentInboxService inbox,
    TimeProvider clock)
{
    public async Task<FinancialEmailCollectorResult?> RunAsync(string profileName, Guid userId, bool dryRun, CancellationToken cancellationToken)
    {
        var profile = options.Sources.SingleOrDefault(candidate => candidate.Enabled && string.Equals(candidate.Name, profileName, StringComparison.OrdinalIgnoreCase));
        if (!options.Enabled || !string.Equals(options.Provider, "Gmail", StringComparison.OrdinalIgnoreCase) || profile is null
            || !await inboxRepository.CanAccessFamilyAsync(profile.FamilyId, userId, cancellationToken)) return null;
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
                    await using var content = await source.OpenAttachmentAsync(message.Id, attachment.Id, cancellationToken);
                    var result = await inbox.ReceiveAutomationAsync(profile.FamilyId, content, attachment.Size, attachment.FileName,
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
            checkpoint.Complete(clock.GetUtcNow(), newestSafeMessage, messages.Count, accepted, failures);
            await checkpoints.SaveAsync(cancellationToken);
        }
        return new(profile.Name, dryRun, messages.Count, candidateMessages, attachmentsScanned, candidates, accepted, duplicates, review, failures, ids, errors);
    }
    private static bool IsPdf(FinancialEmailAttachment attachment) =>
        string.Equals(attachment.ContentType, "application/pdf", StringComparison.OrdinalIgnoreCase)
        || attachment.FileName.EndsWith(".pdf", StringComparison.OrdinalIgnoreCase);
}
