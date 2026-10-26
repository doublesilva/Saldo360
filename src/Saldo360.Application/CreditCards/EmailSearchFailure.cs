using System.Net;
using System.Text.Json;

namespace Saldo360.Application.CreditCards;

public enum EmailFailureStage
{
    Configuration, Credentials, OAuthRefresh, GmailSearch, GmailMessage, GmailAttachments,
    PdfDownload, InboxLookup, InboxReception, DocumentValidation, DocumentProcessing, OperationLock
}

public enum EmailFailureCategory
{
    Missing, FileNotFound, Unreadable, Empty, Unauthorized, Forbidden, RateLimited,
    Timeout, Network, Upstream, InvalidResponse, Io, Failed, Rejected, Conflict, Busy, LimitReached
}

/// <summary>Allowlisted metadata only. Never retains an upstream message, response or inner exception.</summary>
public sealed class EmailSearchFailureException : InvalidOperationException
{
    public EmailFailureStage Stage { get; }
    public EmailFailureCategory Category { get; }
    public string FailureCode { get; }

    public EmailSearchFailureException(EmailFailureStage stage, EmailFailureCategory category)
        : base(Code(stage, category))
    {
        Stage = stage; Category = category; FailureCode = Code(stage, category);
    }

    private static string Code(EmailFailureStage stage, EmailFailureCategory category)
    {
        if (!Enum.IsDefined(category)) throw new ArgumentOutOfRangeException(nameof(category));
        var prefix = stage switch
        {
            EmailFailureStage.Configuration => "GmailConfiguration",
            EmailFailureStage.Credentials => "GmailCredentials",
            EmailFailureStage.OAuthRefresh => "GmailOAuth",
            EmailFailureStage.GmailSearch => "GmailSearch",
            EmailFailureStage.GmailMessage => "GmailMessage",
            EmailFailureStage.GmailAttachments => "GmailAttachments",
            EmailFailureStage.PdfDownload => "EmailPdfDownload",
            EmailFailureStage.InboxLookup => "EmailInboxLookup",
            EmailFailureStage.InboxReception => "EmailInboxReception",
            EmailFailureStage.DocumentValidation => "EmailDocumentValidation",
            EmailFailureStage.DocumentProcessing => "EmailDocumentProcessing",
            EmailFailureStage.OperationLock => "EmailOperationLock",
            _ => throw new ArgumentOutOfRangeException(nameof(stage))
        };
        return prefix + category;
    }

    public static EmailSearchFailureException From(Exception error, EmailFailureStage stage) =>
        error as EmailSearchFailureException ?? new(stage, error switch
        {
            OperationCanceledException or TimeoutException => EmailFailureCategory.Timeout,
            HttpRequestException { StatusCode: HttpStatusCode.Unauthorized } => EmailFailureCategory.Unauthorized,
            HttpRequestException { StatusCode: HttpStatusCode.Forbidden } => EmailFailureCategory.Forbidden,
            HttpRequestException { StatusCode: HttpStatusCode.TooManyRequests } => EmailFailureCategory.RateLimited,
            HttpRequestException { StatusCode: not null } => EmailFailureCategory.Upstream,
            HttpRequestException => EmailFailureCategory.Network,
            JsonException or FormatException or InvalidDataException => EmailFailureCategory.InvalidResponse,
            IOException => EmailFailureCategory.Io,
            _ => EmailFailureCategory.Failed
        });
}
