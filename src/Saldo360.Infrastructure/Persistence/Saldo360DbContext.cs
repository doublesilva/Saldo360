using Microsoft.EntityFrameworkCore;

namespace Saldo360.Infrastructure.Persistence;

public sealed class Saldo360DbContext(DbContextOptions<Saldo360DbContext> options) : DbContext(options)
{
    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        modelBuilder.ApplyConfigurationsFromAssembly(typeof(Saldo360DbContext).Assembly);
    }
}
