using System.Security.Cryptography;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.AspNetCore.WebUtilities;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Saldo360.Infrastructure.Persistence;

namespace Saldo360.Infrastructure.Authentication;

/// <summary>
/// Guarda os tickets de autenticação no PostgreSQL. Remover a linha invalida a sessão de imediato,
/// mesmo que alguém ainda tenha uma cópia do cookie.
/// Toda sessão carrega a identidade externa (issuer + subject) na linha e no ticket; as duas precisam coincidir.
/// </summary>
public sealed class DatabaseTicketStore(
    IServiceScopeFactory scopeFactory,
    IDataProtectionProvider dataProtectionProvider,
    TimeProvider timeProvider,
    ILogger<DatabaseTicketStore> logger) : ITicketStore
{
    public const string ProtectorPurpose = "Saldo360.UserSessions.v1";

    private static readonly TimeSpan FallbackLifetime = TimeSpan.FromHours(8);

    private readonly IDataProtector _protector = dataProtectionProvider.CreateProtector(ProtectorPurpose);

    public async Task<string> StoreAsync(AuthenticationTicket ticket)
    {
        var identity = RequireIdentity(ticket);
        var now = timeProvider.GetUtcNow();
        var session = new UserSession
        {
            Id = WebEncoders.Base64UrlEncode(RandomNumberGenerator.GetBytes(32)),
            Issuer = identity.Issuer,
            Subject = identity.Subject,
            ProtectedTicket = Protect(ticket),
            CreatedAt = now,
            ExpiresAt = ticket.Properties.ExpiresUtc ?? now.Add(FallbackLifetime),
        };

        await using var scope = scopeFactory.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<Saldo360DbContext>();
        db.UserSessions.Add(session);
        await db.SaveChangesAsync();
        return session.Id;
    }

    public async Task RenewAsync(string key, AuthenticationTicket ticket)
    {
        await using var scope = scopeFactory.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<Saldo360DbContext>();
        var session = await db.UserSessions.FindAsync(key);
        if (session is null)
        {
            return;
        }

        // A renovação nunca troca a identidade da sessão.
        if (!ExternalIdentity.TryFromPrincipal(ticket.Principal, out var identity) || !Matches(session, identity))
        {
            logger.LogWarning("Renovação recusada: a identidade do ticket não corresponde à da sessão. Sessão removida.");
            db.UserSessions.Remove(session);
            await db.SaveChangesAsync();
            return;
        }

        session.ProtectedTicket = Protect(ticket);
        session.ExpiresAt = ticket.Properties.ExpiresUtc ?? timeProvider.GetUtcNow().Add(FallbackLifetime);
        await db.SaveChangesAsync();
    }

    public async Task<AuthenticationTicket?> RetrieveAsync(string key)
    {
        await using var scope = scopeFactory.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<Saldo360DbContext>();
        var session = await db.UserSessions.FindAsync(key);
        if (session is null)
        {
            return null;
        }

        var ticket = Validate(session, out var rejectionReason);
        if (ticket is not null)
        {
            return ticket;
        }

        if (rejectionReason is not null)
        {
            logger.LogInformation("Sessão recusada ({Reason}); um novo login será exigido.", rejectionReason);
        }

        db.UserSessions.Remove(session);
        await db.SaveChangesAsync();
        return null;
    }

    public async Task RemoveAsync(string key)
    {
        await using var scope = scopeFactory.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<Saldo360DbContext>();
        var session = await db.UserSessions.FindAsync(key);
        if (session is not null)
        {
            db.UserSessions.Remove(session);
            await db.SaveChangesAsync();
        }
    }

    /// <summary>
    /// Devolve o ticket se a sessão for utilizável. Caso contrário devolve null e, exceto para sessões
    /// apenas expiradas, um motivo para o log (sem dados do usuário).
    /// </summary>
    private AuthenticationTicket? Validate(UserSession session, out string? rejectionReason)
    {
        rejectionReason = null;
        if (session.ExpiresAt <= timeProvider.GetUtcNow())
        {
            return null;
        }

        // Sessões anteriores ao registro do emissor (issuer nulo) caem aqui.
        if (!ExternalIdentity.IsValid(session.Issuer) || !ExternalIdentity.IsValid(session.Subject))
        {
            rejectionReason = "registro sem issuer ou subject";
            return null;
        }

        AuthenticationTicket? ticket;
        try
        {
            ticket = TicketSerializer.Default.Deserialize(_protector.Unprotect(session.ProtectedTicket));
        }
        catch (CryptographicException)
        {
            // Chave de Data Protection perdida ou rotacionada: trata como sessão inexistente.
            rejectionReason = "ticket ilegível";
            return null;
        }

        if (ticket is null || !ExternalIdentity.TryFromPrincipal(ticket.Principal, out var identity))
        {
            rejectionReason = "ticket sem issuer ou subject";
            return null;
        }

        if (!Matches(session, identity))
        {
            rejectionReason = "identidade do ticket diverge do registro";
            return null;
        }

        return ticket;
    }

    private static ExternalIdentity RequireIdentity(AuthenticationTicket ticket) =>
        ExternalIdentity.TryFromPrincipal(ticket.Principal, out var identity)
            ? identity
            : throw new InvalidOperationException("Sessões exigem exatamente uma claim 'iss' e uma 'sub' preenchidas.");

    // Comparação ordinal: issuer e subject são opacos.
    private static bool Matches(UserSession session, ExternalIdentity identity) =>
        string.Equals(session.Issuer, identity.Issuer, StringComparison.Ordinal)
        && string.Equals(session.Subject, identity.Subject, StringComparison.Ordinal);

    private byte[] Protect(AuthenticationTicket ticket) =>
        _protector.Protect(TicketSerializer.Default.Serialize(ticket));
}
