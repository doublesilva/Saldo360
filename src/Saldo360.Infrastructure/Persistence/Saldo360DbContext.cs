using Microsoft.AspNetCore.DataProtection.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore;
using Saldo360.Domain.Families;
using Saldo360.Domain.Users;
using Saldo360.Infrastructure.Authentication;

namespace Saldo360.Infrastructure.Persistence;

public sealed class Saldo360DbContext(DbContextOptions<Saldo360DbContext> options)
    : DbContext(options), IDataProtectionKeyContext
{
    public DbSet<UserSession> UserSessions => Set<UserSession>();

    /// <summary>Chaves do Data Protection, compartilhadas entre instâncias e reinícios da API.</summary>
    public DbSet<DataProtectionKey> DataProtectionKeys => Set<DataProtectionKey>();

    public DbSet<ApplicationUser> Users => Set<ApplicationUser>();

    public DbSet<Family> Families => Set<Family>();

    public DbSet<FamilyMember> FamilyMembers => Set<FamilyMember>();

    public DbSet<FamilyInvitation> FamilyInvitations => Set<FamilyInvitation>();

    /// <summary>Configuração do provedor usada pela API, pelas ferramentas de migration e pelos testes.</summary>
    public static DbContextOptionsBuilder Configure(DbContextOptionsBuilder builder, string connectionString) =>
        builder.UseNpgsql(connectionString).UseSnakeCaseNamingConvention();

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        modelBuilder.ApplyConfigurationsFromAssembly(typeof(Saldo360DbContext).Assembly);
    }
}
