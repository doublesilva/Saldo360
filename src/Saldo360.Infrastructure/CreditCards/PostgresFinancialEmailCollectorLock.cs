using System.Security.Cryptography;
using System.Text;
using Npgsql;
using Saldo360.Application.CreditCards;

namespace Saldo360.Infrastructure.CreditCards;

internal sealed class PostgresFinancialEmailCollectorLock(string connectionString) : IFinancialEmailCollectorLock
{
    public async Task<IAsyncDisposable?> TryAcquireAsync(Guid workspaceId, string profileName, CancellationToken cancellationToken)
    {
        var hash = SHA256.HashData(Encoding.UTF8.GetBytes($"{workspaceId:N}:{profileName.ToUpperInvariant()}"));
        var key1 = BitConverter.ToInt32(hash, 0); var key2 = BitConverter.ToInt32(hash, 4);
        var connection = new NpgsqlConnection(connectionString); await connection.OpenAsync(cancellationToken);
        await using var command = new NpgsqlCommand("SELECT pg_try_advisory_lock(@key1, @key2)", connection);
        command.Parameters.AddWithValue("key1", key1); command.Parameters.AddWithValue("key2", key2);
        if (await command.ExecuteScalarAsync(cancellationToken) is not true) { await connection.DisposeAsync(); return null; }
        return new Lease(connection, key1, key2);
    }
    private sealed class Lease(NpgsqlConnection connection, int key1, int key2) : IAsyncDisposable
    {
        public async ValueTask DisposeAsync()
        {
            try { await using var command = new NpgsqlCommand("SELECT pg_advisory_unlock(@key1, @key2)", connection); command.Parameters.AddWithValue("key1", key1); command.Parameters.AddWithValue("key2", key2); await command.ExecuteScalarAsync(); }
            finally { await connection.DisposeAsync(); }
        }
    }
}
