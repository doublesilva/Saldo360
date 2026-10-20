using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Design;

namespace Saldo360.Infrastructure.Persistence;

/// <summary>Offline model generation: never reads production configuration or opens a connection.</summary>
public sealed class DesignTimeWorkspaceFactory : IDesignTimeDbContextFactory<Saldo360DbContext>
{
    public Saldo360DbContext CreateDbContext(string[] args)
    {
        var options = new DbContextOptionsBuilder<Saldo360DbContext>();
        Saldo360DbContext.Configure(options, "Host=127.0.0.1;Database=saldo360_design_only;Username=design_only;Password=not-a-real-secret");
        return new(options.Options);
    }
}
