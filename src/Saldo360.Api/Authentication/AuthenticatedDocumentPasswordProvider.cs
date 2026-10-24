using Saldo360.Application.CreditCards;
using Saldo360.Application.Users;
using Saldo360.Application.Workspaces;

namespace Saldo360.Api.Authentication;

/// <summary>CPF belongs only to the authenticated caller, never to an arbitrary card holder ID.</summary>
public sealed class AuthenticatedDocumentPasswordProvider(IHttpContextAccessor http, CurrentUser currentUser, ICurrentUserCpf cpf,
    IFinancialWorkspaceRepository workspaces, ICreditCardPdfContextRepository cards, IFinancialDocumentInboxRepository documents) : ICreditCardPdfPasswordProvider
{
    public Task<CreditCardPdfPasswordResolution> ResolveAsync(Guid holderUserId, string issuer, CancellationToken ct) =>
        Task.FromResult(new CreditCardPdfPasswordResolution([], "AuthenticatedDocumentContextRequired"));

    public async Task<CreditCardPdfPasswordResolution> ResolveForDocumentAsync(PdfPasswordContext context, string? manualPassword, CancellationToken ct)
    {
        if (http.HttpContext?.User.Identity?.IsAuthenticated != true || context.UserId == Guid.Empty || await currentUser.GetUserIdAsync(ct) != context.UserId
            || await workspaces.GetOwnerRoleAsync(context.WorkspaceId, context.UserId, ct) is null
            || context.CardId is { } card && await cards.GetAsync(context.WorkspaceId, card, context.UserId, ct) is null
            || context.DocumentId is { } document && await documents.GetAsync(context.WorkspaceId, document, context.UserId, ct) is null)
            return new([], "CpfAccessDenied");
        if (!string.IsNullOrEmpty(manualPassword)) return new([manualPassword]);
        UserCpfResult result;
        try { result = await cpf.GetAsync(ct); }
        catch (Exception error) when (error is not OperationCanceledException) { return new([], "CpfUnavailable"); }
        if (result.Status != UserCpfStatus.Available) return new([], result.Status switch
        {
            UserCpfStatus.NotRegistered => "CpfNotRegistered", UserCpfStatus.SessionInvalid => "CpfSessionInvalid",
            UserCpfStatus.AccessDenied => "CpfAccessDenied", _ => "CpfUnavailable"
        });
        // Accept only canonical digits or the conventional punctuation; never strip arbitrary characters.
        var raw = result.FullValue;
        var digits = raw?.Length == 14 && raw[3] == '.' && raw[7] == '.' && raw[11] == '-'
            ? raw.Replace(".", "", StringComparison.Ordinal).Replace("-", "", StringComparison.Ordinal) : raw;
        if (!ValidCpf(digits)) return new([], "CpfInvalid");
        return new([digits![..3], digits[..5]]);
    }

    private static bool ValidCpf(string? value)
    {
        if (value is null || value.Length != 11 || value.Any(c => !char.IsAsciiDigit(c)) || value.Distinct().Count() == 1) return false;
        for (var n = 9; n <= 10; n++)
        { var sum = 0; for (var i = 0; i < n; i++) sum += (value[i] - '0') * (n + 1 - i); var digit = sum * 10 % 11; if (digit == 10) digit = 0; if (value[n] - '0' != digit) return false; }
        return true;
    }
}
