using Microsoft.EntityFrameworkCore;
using Npgsql;
using Saldo360.Application.Users;
using Saldo360.Domain.Users;
using Saldo360.Infrastructure.Persistence;

namespace Saldo360.Infrastructure.Users;

/// <summary>
/// Busca ou cria o usuário interno pela identidade externa. A unicidade é garantida pelo índice único
/// (issuer, subject) do PostgreSQL: se duas requisições criarem ao mesmo tempo, a que perder recebe
/// unique_violation, descarta a própria inserção e lê o registro vencedor.
/// </summary>
internal sealed class UserDirectory(Saldo360DbContext db, TimeProvider timeProvider) : IUserDirectory
{
    public async Task<Guid> ResolveAsync(string issuer, string subject, string? displayName, CancellationToken cancellationToken)
    {
        // "==" vira "=" no PostgreSQL: comparação exata, sensível à caixa.
        var existing = await db.Users
            .SingleOrDefaultAsync(user => user.Issuer == issuer && user.Subject == subject, cancellationToken);
        if (existing is not null)
        {
            if (existing.UpdateDisplayName(displayName))
            {
                await db.SaveChangesAsync(cancellationToken);
            }

            return existing.Id;
        }

        var created = ApplicationUser.Create(issuer, subject, displayName, timeProvider.GetUtcNow());
        db.Users.Add(created);
        try
        {
            await db.SaveChangesAsync(cancellationToken);
            return created.Id;
        }
        catch (DbUpdateException exception) when (IsUniqueViolation(exception))
        {
            db.Entry(created).State = EntityState.Detached;
            return await db.Users
                .Where(user => user.Issuer == issuer && user.Subject == subject)
                .Select(user => user.Id)
                .SingleAsync(cancellationToken);
        }
    }

    private static bool IsUniqueViolation(DbUpdateException exception) =>
        exception.InnerException is PostgresException { SqlState: PostgresErrorCodes.UniqueViolation };
}
