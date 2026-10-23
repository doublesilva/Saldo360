using System.Security.Cryptography;

namespace Saldo360.Application.CreditCards;

public sealed class LatestEmailSearchLimitException : Exception;

public sealed record CardLatestEmailResult(string Status, Guid CardId, Guid? DocumentId = null,
    string? ReceivedAt = null, string? FileName = null, string? InboxStatus = null,
    bool AlreadyExisted = false, string? FailureCode = null);

/// <summary>Explicit owner action; deliberately has no collector, checkpoint or financial writer dependency.</summary>
public sealed class CardLatestEmailService(
    CardEmailSearchAuthorization authorization,
    IFinancialCreditCardRepository cards,
    IFinancialEmailSource source,
    ICreditCardPdfPreviewService preview,
    ICardEmailDocumentReceiver inbox,
    IFinancialEmailCollectorLock operationLock)
{
    public const int MessageLimit = 200;

    public async Task<CardLatestEmailResult> SearchAsync(Guid workspaceId, Guid cardId, Guid userId, CancellationToken ct)
    {
        // Must precede all mailbox access, including attachment metadata and existing document lookup.
        if (!await authorization.CanSearchAsync(workspaceId, userId, ct)) return new("NotFound", cardId);
        var card = await cards.GetAsync(workspaceId, cardId, ct);
        if (card is null || card.IsArchived) return new("NotFound", cardId);
        if (string.IsNullOrWhiteSpace(card.GmailQuery)) return new("QueryMissing", cardId, FailureCode: "QueryMissing");
        await using var lease = await operationLock.TryAcquireAsync(workspaceId, $"card-latest:{cardId:N}", ct);
        if (lease is null) return new("Busy", cardId, FailureCode: "AlreadyRunning");
        try
        {
            var messages = await source.SearchLatestAsync(card.GmailQuery, MessageLimit, ct);
            foreach (var message in messages.OrderByDescending(x => x.ReceivedAt).ThenBy(x => x.Id, StringComparer.Ordinal))
            {
                var attachments = await source.ListAttachmentsAsync(message.Id, ct);
                if (attachments.Count > 20) return new("Error", cardId, FailureCode: "AttachmentLimitReached");
                var candidates = new List<(FinancialEmailAttachment Attachment, MemoryStream Content, bool Review)>();
                try
                {
                    foreach (var attachment in attachments.OrderBy(x => x.PartId, StringComparer.Ordinal))
                    {
                        if (!attachment.FileName.EndsWith(".pdf", StringComparison.OrdinalIgnoreCase)
                            || attachment.Size <= 0 || attachment.Size > CreditCardPdfPreviewService.MaxFileBytes) continue;
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
                            var known = await inbox.FindKnownEmailAsync(workspaceId, userId,
                                GmailAttachmentIdentity.Create(message.Id, attachment.PartId), hash, ct);
                            if (known is not null)
                            {
                                if (known.ContentHash != hash) { content.Dispose(); return new("Error", cardId, FailureCode: "DocumentConflict"); }
                                if (known.CreditCardId is { } knownCard && knownCard != cardId) { content.Dispose(); continue; }
                                candidates.Add((attachment, content, known.CreditCardId != cardId));
                                continue;
                            }
                            content.Position = 0;
                            var check = await preview.PreviewAsync(workspaceId, cardId, userId, content, content.Length,
                                attachment.FileName, null, null, null, null, ct);
                            if (check.Access == CreditCardPdfPreviewAccess.NotFound) { content.Dispose(); return new("NotFound", cardId); }
                            if (check.Access != CreditCardPdfPreviewAccess.Allowed && check.FailureCode is not ("PasswordRequired" or "TextUnavailable" or "UnsupportedEncryption")) { content.Dispose(); continue; }
                            var match = check.Preview?.CardResolution;
                            candidates.Add((attachment, content, match?.Status != "Matched" || match.CardId != cardId));
                        }
                        catch { content.Dispose(); throw; }
                    }
                    if (candidates.Count == 0) continue;
                    // No arbitrary choice between several PDFs in the same message.
                    var matched = candidates.Where(x => !x.Review).ToArray();
                    if (matched.Length > 1 || (matched.Length == 0 && candidates.Count > 1))
                        return new("NeedsReview", cardId, FailureCode: "AmbiguousAttachments");
                    var chosen = matched.Length == 1 ? matched[0] : candidates[0];
                    chosen.Content.Position = 0;
                    var received = await inbox.ReceiveEmailPendingAsync(workspaceId, userId, chosen.Content, chosen.Content.Length,
                        chosen.Attachment.FileName, GmailAttachmentIdentity.Create(message.Id, chosen.Attachment.PartId),
                        message.ReceivedAt, chosen.Review, ct);
                    if (received.Result == "NotFound") return new("NotFound", cardId);
                    if (received.Result is not ("Created" or "AlreadyExists") || received.Item is null)
                        return new("Error", cardId, FailureCode: received.Result == "Conflict" ? "DocumentConflict" : "ReceptionFailed");
                    var item = received.Item;
                    return new(received.Result == "AlreadyExists" ? "AlreadyReceived" : chosen.Review ? "NeedsReview" : "Found",
                        cardId, item.Id, item.ReceivedAt, item.OriginalFileName, item.Status, received.Result == "AlreadyExists",
                        chosen.Review ? "CardIdentificationRequired" : null);
                }
                finally { foreach (var candidate in candidates) candidate.Content.Dispose(); }
            }
            return new("NoMatch", cardId);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
        catch (LatestEmailSearchLimitException) { return new("Error", cardId, FailureCode: "SearchLimitReached"); }
        catch (Exception) { return new("Unavailable", cardId, FailureCode: "EmailSearchUnavailable"); }
    }
}
