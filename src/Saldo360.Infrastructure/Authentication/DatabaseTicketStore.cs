using System.Security.Claims;
using System.Security.Cryptography;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.AspNetCore.WebUtilities;
using Microsoft.Extensions.DependencyInjection;
using Saldo360.Infrastructure.Persistence;

namespace Saldo360.Infrastructure.Authentication;

/// <summary>
/// Guarda os tickets de autenticação no PostgreSQL. Remover a linha invalida a sessão de imediato,
/// mesmo que alguém ainda tenha uma cópia do cookie.
/// </summary>
public sealed class DatabaseTicketStore(
    IServiceScopeFactory scopeFactory,
    IDataProtectionProvider dataProtectionProvider,
    TimeProvider timeProvider) : ITicketStore
{
    private static readonly TimeSpan FallbackLifetime = TimeSpan.FromHours(8);

    private readonly IDataProtector _protector = dataProtectionProvider.CreateProtector("Saldo360.UserSessions.v1");

    public async Task<string> StoreAsync(AuthenticationTicket ticket)
    {
        var now = timeProvider.GetUtcNow();
        var session = new UserSession
        {
            Id = WebEncoders.Base64UrlEncode(RandomNumberGenerator.GetBytes(32)),
            Subject = ticket.Principal.FindFirstValue("sub") ?? string.Empty,
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

        if (session.ExpiresAt <= timeProvider.GetUtcNow())
        {
            db.UserSessions.Remove(session);
            await db.SaveChangesAsync();
            return null;
        }

        try
        {
            return TicketSerializer.Default.Deserialize(_protector.Unprotect(session.ProtectedTicket));
        }
        catch (CryptographicException)
        {
            // Chave de Data Protection perdida ou rotacionada: trata como sessão inexistente.
            return null;
        }
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

    private byte[] Protect(AuthenticationTicket ticket) =>
        _protector.Protect(TicketSerializer.Default.Serialize(ticket));
}
