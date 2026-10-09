using Microsoft.AspNetCore.DataProtection.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore;
using Saldo360.Infrastructure.Authentication;

namespace Saldo360.Infrastructure.Persistence;

public sealed class Saldo360DbContext(DbContextOptions<Saldo360DbContext> options)
    : DbContext(options), IDataProtectionKeyContext
{
    public DbSet<UserSession> UserSessions => Set<UserSession>();

    /// <summary>Chaves do Data Protection, compartilhadas entre instâncias e reinícios da API.</summary>
    public DbSet<DataProtectionKey> DataProtectionKeys => Set<DataProtectionKey>();

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        modelBuilder.ApplyConfigurationsFromAssembly(typeof(Saldo360DbContext).Assembly);
    }
}
