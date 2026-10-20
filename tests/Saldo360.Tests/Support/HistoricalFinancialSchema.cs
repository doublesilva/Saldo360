using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using Npgsql;
using Saldo360.Infrastructure.Persistence;

namespace Saldo360.Tests.Support;

/// <summary>Only disposable Testcontainers fixture data. Never used by application or cutover tooling.</summary>
internal static class HistoricalFinancialSchema
{
    public static async Task PrepareAsync(Saldo360DbContext db, string migration)
    {
        var database = new NpgsqlConnectionStringBuilder(db.Database.GetConnectionString()).Database;
        if (database is null || !database.StartsWith("saldo360_", StringComparison.Ordinal))
            throw new InvalidOperationException("Disposable test database required.");
        var roots = await db.FinancialWorkspaces.AsNoTracking().ToListAsync();
        var accounts = await db.FinancialAccounts.AsNoTracking().ToListAsync();
        var categories = await db.FinancialCategories.AsNoTracking().ToListAsync();
        // These historical fixtures contain only identities, empty roots, accounts and categories.
        await db.FinancialAccounts.ExecuteDeleteAsync();
        await db.FinancialCategories.ExecuteDeleteAsync();
        await db.FinancialWorkspaces.ExecuteDeleteAsync();
        await db.GetService<IMigrator>().MigrateAsync(migration);
        foreach (var root in roots)
        {
            await db.Database.ExecuteSqlInterpolatedAsync($"""
                INSERT INTO families (id, name, created_at) VALUES ({root.Id}, 'Historical fixture', now());
                INSERT INTO family_members (id, family_id, user_id, role, joined_at)
                VALUES ({root.OwnerUserId}, {root.Id}, {root.OwnerUserId}, 'Owner', now());
                """);
        }
        foreach (var account in accounts)
        {
            await db.Database.ExecuteSqlInterpolatedAsync($"""
                INSERT INTO financial_accounts (id, family_id, name, institution, type, scope, responsible_member_id, is_active, currency, created_at, created_by_user_id)
                VALUES ({account.Id}, {account.WorkspaceId}, {account.Name}, {account.Institution}, {account.Type.ToString()}, {account.Scope.ToString()}, {account.ResponsibleUserId}, {account.IsActive}, 'BRL', {account.CreatedAt}, {account.CreatedByUserId});
                """);
            if (account.OpeningBalance is not null)
                await db.Database.ExecuteSqlInterpolatedAsync($"UPDATE financial_accounts SET opening_balance={account.OpeningBalance}, opening_balance_date={account.OpeningBalanceDate} WHERE id={account.Id}");
        }
        foreach (var category in categories)
            await db.Database.ExecuteSqlInterpolatedAsync($"""
                INSERT INTO financial_categories (id, family_id, name, type, is_active, created_at, created_by_user_id)
                VALUES ({category.Id}, {category.WorkspaceId}, {category.Name}, {category.Type.ToString()}, {category.IsActive}, {category.CreatedAt}, {category.CreatedByUserId});
                """);
        // The old fixture IDs are historical Family IDs, never reused as destination workspace IDs.
        var plan = JsonSerializer.Serialize(roots.Select(root => new { family_id = root.Id, workspace_id = Guid.CreateVersion7(), owner_user_id = root.OwnerUserId }));
        await db.Database.OpenConnectionAsync();
        await db.Database.ExecuteSqlInterpolatedAsync($"SELECT set_config('saldo360.workspace_cutover_plan', {plan}, false)");
    }
}
