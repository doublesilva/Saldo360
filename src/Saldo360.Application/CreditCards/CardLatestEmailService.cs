using System.Security.Cryptography;
using Microsoft.Extensions.Logging;

namespace Saldo360.Application.CreditCards;

public sealed class LatestEmailSearchLimitException : Exception;

public sealed record CardLatestEmailResult(string Status, Guid CardId, Guid? DocumentId = null,
    string? ReceivedAt = null, string? FileName = null, string? InboxStatus = null,
    bool AlreadyExisted = false, string? FailureCode = null)
{
    public InboxStatementProcessingResult? Processing { get; init; }
    public string? FailureStage { get; init; }
    public string? FailureCategory { get; init; }
}

/// <summary>Explicit owner action; reuses statement processing without incremental collectors/checkpoints.</summary>
public sealed class CardLatestEmailService(
    CardEmailSearchAuthorization authorization,
    IFinancialCreditCardRepository cards,
    IFinancialEmailSource source,
    ICreditCardPdfPreviewService preview,
    ICardEmailDocumentReceiver inbox,
    IFinancialEmailCollectorLock operationLock,
    IInboxStatementProcessingService processing, CreditCardStatementAgentOptions? statementOptions = null,
    ICreditCardPdfTextExtractor? documentReader = null, ILogger<CardLatestEmailService>? logger = null)
{
    public const int MessageLimit = 200;

    public async Task<CardLatestEmailResult> SearchAsync(Guid workspaceId, Guid cardId, Guid userId, CancellationToken ct)
    {
        // Must precede all mailbox access, including attachment metadata and existing document lookup.
        if (!await authorization.CanSearchAsync(workspaceId, userId, ct)) return new("NotFound", cardId);
        var card = await cards.GetAsync(workspaceId, cardId, ct);
        if (card is null || card.IsArchived) return new("NotFound", cardId);
        if (string.IsNullOrWhiteSpace(card.GmailQuery)) return Failure(cardId, new(EmailFailureStage.Configuration, EmailFailureCategory.Missing), "QueryMissing", "QueryMissing");
        var stage = EmailFailureStage.OperationLock;
        FinancialDocumentInboxDto? receivedItem = null;
        var alreadyReceived = false;
        try
        {
            await using var lease = await operationLock.TryAcquireAsync(workspaceId, $"card-latest:{cardId:N}", ct);
            if (lease is null) return Failure(cardId, new(stage, EmailFailureCategory.Busy), "Busy", "AlreadyRunning");
            stage = EmailFailureStage.GmailSearch;
            var messages = await source.SearchLatestAsync(card.GmailQuery, MessageLimit, ct);
            foreach (var message in messages.OrderByDescending(x => x.ReceivedAt).ThenBy(x => x.Id, StringComparer.Ordinal))
            {
                stage = EmailFailureStage.GmailAttachments;
                var attachments = await source.ListAttachmentsAsync(message.Id, ct);
                if (attachments.Count > 20) return Failure(cardId, new(stage, EmailFailureCategory.LimitReached), "Error", "AttachmentLimitReached");
                var candidates = new List<(FinancialEmailAttachment Attachment, MemoryStream Content, bool Review)>();
                try
                {
                    foreach (var attachment in attachments.OrderBy(x => x.PartId, StringComparer.Ordinal))
                    {
                        if (!attachment.FileName.EndsWith(".pdf", StringComparison.OrdinalIgnoreCase)
                            || attachment.Size <= 0 || attachment.Size > CreditCardPdfPreviewService.MaxFileBytes) continue;
                        stage = EmailFailureStage.PdfDownload;
                        await using var stream = await source.OpenAttachmentAsync(message.Id, attachment.Id, ct);
                        var content = new MemoryStream();
                        try
                        {
                            var chunk = new byte[81920]; int read;
                            while ((read = await stream.ReadAsync(chunk, ct)) > 0)
                            {
                                if (content.Length + read > CreditCardPdfPreviewService.MaxFileBytes) throw new InvalidDataException("Attachment limit exceeded.");
                                await content.WriteAsync(chunk.AsMemory(0, read), ct);
                            }
                            if (content.Length < 5 || !content.GetBuffer().AsSpan(0, 5).SequenceEqual("%PDF-"u8)) { content.Dispose(); continue; }
                            var hash = Convert.ToHexStringLower(SHA256.HashData(content.GetBuffer().AsSpan(0, (int)content.Length)));
                            stage = EmailFailureStage.InboxLookup;
                            var known = await inbox.FindKnownEmailAsync(workspaceId, userId,
                                GmailAttachmentIdentity.Create(message.Id, attachment.PartId), hash, ct);
                            if (known is not null)
                            {
                                if (known.ContentHash != hash) { content.Dispose(); return Failure(cardId, new(stage, EmailFailureCategory.Conflict), "Error", "DocumentConflict"); }
                                if (known.CreditCardId is { } knownCard && knownCard != cardId) { content.Dispose(); continue; }
                                candidates.Add((attachment, content, known.CreditCardId != cardId));
                                continue;
                            }
                            content.Position = 0;
                            stage = EmailFailureStage.DocumentValidation;
                            if (statementOptions?.DocumentaryImport.Enabled == true)
                            {
                                // Container validation only. Never run the traditional financial parser before this profile.
                                if (documentReader is null) { content.Dispose(); return Failure(cardId, new(stage, EmailFailureCategory.Failed), compatibilityCode: "DocumentaryPipelineUnavailable"); }
                                try { await documentReader.ExtractAsync(content, CreditCardPdfPreviewService.MaxPages, [], ct); }
                                catch (CreditCardPdfReadException e) when (e.Failure is CreditCardPdfReadFailure.PasswordRequired or CreditCardPdfReadFailure.PasswordRejected) { }
                                catch (CreditCardPdfReadException) { content.Dispose(); return Failure(cardId, new(stage, EmailFailureCategory.Rejected), "Error"); }
                                candidates.Add((attachment, content, true));
                                continue;
                            }
                            var check = await preview.PreviewAsync(workspaceId, cardId, userId, content, content.Length,
                                attachment.FileName, null, null, null, null, ct);
                            if (check.Access == CreditCardPdfPreviewAccess.NotFound) { content.Dispose(); return new("NotFound", cardId); }
                            if (check.Access != CreditCardPdfPreviewAccess.Allowed && check.Errors?.ContainsKey("cardId") != true
                                && check.FailureCode is not ("PasswordRequired" or "PasswordRejected" or "TextUnavailable" or "UnsupportedEncryption" or "InsufficientEvidence" or "CardIdentificationRequired")) { content.Dispose(); continue; }
                            var match = check.Preview?.CardResolution;
                            candidates.Add((attachment, content, match?.Status != "Matched" || match.CardId != cardId));
                        }
                        catch { content.Dispose(); throw; }
                    }
                    if (candidates.Count == 0) continue;
                    // No arbitrary choice between several PDFs in the same message.
                    var matched = candidates.Where(x => !x.Review).ToArray();
                    if (matched.Length > 1 || (matched.Length == 0 && candidates.Count > 1))
                        return Failure(cardId, new(EmailFailureStage.DocumentValidation, EmailFailureCategory.Rejected), "NeedsReview", "AmbiguousAttachments");
                    var chosen = matched.Length == 1 ? matched[0] : candidates[0];
                    chosen.Content.Position = 0;
                    stage = EmailFailureStage.InboxReception;
                    var received = await inbox.ReceiveEmailPendingAsync(workspaceId, userId, chosen.Content, chosen.Content.Length,
                        chosen.Attachment.FileName, GmailAttachmentIdentity.Create(message.Id, chosen.Attachment.PartId),
                        message.ReceivedAt, chosen.Review, ct);
                    if (received.Result == "NotFound") return new("NotFound", cardId);
                    if (received.Result is not ("Created" or "AlreadyExists") || received.Item is null)
                        return Failure(cardId, new(stage, received.Result == "Conflict" ? EmailFailureCategory.Conflict : EmailFailureCategory.Rejected), "Error");
                    var item = received.Item;
                    receivedItem = item;
                    alreadyReceived = received.Result == "AlreadyExists";
                    if (item.Status == "Imported")
                        return new("AlreadyReceived", cardId, item.Id, item.ReceivedAt, item.OriginalFileName, item.Status, true);
                    stage = EmailFailureStage.DocumentProcessing;
                    var processed = await processing.AnalyzeOnlyAsync(workspaceId, item.Id, cardId, userId, null, ct);
                    // A received document is not proof that analysis/consolidation succeeded.
                    if (ProcessingFailure(processed) is { } processingFailure)
                    {
                        var recoverable = processed.Analysis.Analysis?.FailureCode is "PasswordRequired" or "PasswordRejected";
                        return Failure(cardId, new(stage, processingFailure), recoverable ? "NeedsReview" : processingFailure == EmailFailureCategory.Busy ? "Busy" : "Error")
                            with { DocumentId = item.Id, AlreadyExisted = received.Result == "AlreadyExists", InboxStatus = item.Status,
                                Processing = recoverable ? processed : null };
                    }
                    var code = processed.Consolidation.FailureCode ?? processed.Analysis.Analysis?.FailureCode;
                    var status = processed.Consolidation.Status is "Imported" or "AlreadyImported" or "NewVersionConsolidated" or "InvoicesLinked" ? "Imported"
                        : processed.Analysis.Status is "Reliable" or "AlreadyAnalyzed" ? "ReadyForReview"
                        : processed.Analysis.Status == "Busy" ? item.Status : "NeedsReview";
                    return new(received.Result == "AlreadyExists" ? "AlreadyReceived" : "Found",
                        cardId, item.Id, item.ReceivedAt, item.OriginalFileName, status, received.Result == "AlreadyExists",
                        code) { Processing = processed };
                }
                finally { foreach (var candidate in candidates) candidate.Content.Dispose(); }
            }
            return new("NoMatch", cardId);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
        catch (LatestEmailSearchLimitException) { return Failure(cardId, new(EmailFailureStage.GmailSearch, EmailFailureCategory.LimitReached), "Error", "SearchLimitReached"); }
        catch (Exception error)
        {
            return Failure(cardId, EmailSearchFailureException.From(error, stage))
                with { DocumentId = receivedItem?.Id, AlreadyExisted = alreadyReceived, InboxStatus = receivedItem?.Status };
        }
    }

    private CardLatestEmailResult Failure(Guid cardId, EmailSearchFailureException failure, string status = "Unavailable", string? compatibilityCode = null)
    {
        // No exception parameter: even inner exceptions and exception type names are excluded.
        logger?.LogWarning(new EventId(16051, "CardLatestEmailFailure"),
            "Card latest email failed at {Stage} with {Category}; code {FailureCode}",
            failure.Stage.ToString(), failure.Category.ToString(), compatibilityCode ?? failure.FailureCode);
        return new(status, cardId, FailureCode: compatibilityCode ?? failure.FailureCode)
        { FailureStage = failure.Stage.ToString(), FailureCategory = failure.Category.ToString() };
    }

    private static EmailFailureCategory? ProcessingFailure(InboxStatementProcessingResult result)
    {
        // Legacy analysis refuses to re-analyze an imported document (Conflict). The atomic
        // repository may then attest the exact existing import. This is replay, not a failed write.
        if (result.Analysis.Status == "Conflict" && result.Consolidation.Status == "AlreadyImported") return null;
        if (result.Analysis.Status == "Busy" || result.Consolidation.Status == "Busy") return EmailFailureCategory.Busy;
        if (result.Analysis.Status is "NotFound" or "Conflict" || result.Consolidation.Status is "NotFound" or "Conflict") return EmailFailureCategory.Conflict;
        if (result.Analysis.Status is "Failed" or "Error" or "Unavailable" || result.Consolidation.Status is "Failed" or "Error" or "Unavailable") return EmailFailureCategory.Failed;
        if (result.Analysis.Status is "Rejected" or "Invalid" || result.Analysis.Analysis?.Status == "Rejected" || result.Consolidation.Status is "Rejected" or "Invalid") return EmailFailureCategory.Rejected;
        if (result.Analysis.Analysis?.FailureCode is not null) return EmailFailureCategory.Failed;
        // NeedsReview without an analysis can also represent a swallowed provider/preparation error.
        if (result.Analysis.Analysis is null && result.Consolidation.FailureCode is not (null or "AnalysisRequired")) return EmailFailureCategory.Failed;
        if (result.Analysis.Status is not ("Reliable" or "AlreadyAnalyzed" or "AlreadyImported" or "NeedsReview")
            || result.Consolidation.Status is not ("Imported" or "AlreadyImported" or "NewVersionConsolidated" or "InvoicesLinked" or "AwaitingConfirmation" or "NeedsReview")) return EmailFailureCategory.Failed;
        return null;
    }
}
