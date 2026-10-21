using System.Data.Common;
using System.Globalization;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Npgsql;
using Saldo360.Application.Workspaces;

namespace Saldo360.Infrastructure.Workspaces;

internal sealed class FinancialWriteConnectionInterceptor : DbConnectionInterceptor
{
    private static DbCommand Capture(DbConnection connection, FinancialWriteScope.Context scope)
    {
        var command = connection.CreateCommand();
        command.CommandText = "SELECT clock_timestamp(), (SELECT xmin::text::bigint FROM financial_workspaces WHERE id=@w)";
        command.Parameters.Add(new NpgsqlParameter("w", scope.WorkspaceId ?? Guid.Empty));
        return command;
    }
    private static DbCommand Command(DbConnection connection)
    {
        var command = connection.CreateCommand();
        command.CommandText = "SELECT set_config('saldo360.writer_started_at', @started, false), set_config('saldo360.writer_workspace_version', @version, false)";
        var parameter = command.CreateParameter(); parameter.ParameterName = "started";
        parameter.Value = FinancialWriteScope.Current?.DatabaseStartedAt?.ToString("O", CultureInfo.InvariantCulture) ?? "";
        command.Parameters.Add(new NpgsqlParameter("version", FinancialWriteScope.Current?.ExpectedVersion?.ToString(CultureInfo.InvariantCulture) ?? ""));
        command.Parameters.Add(parameter); return command;
    }
    public override void ConnectionOpened(DbConnection connection, ConnectionEndEventData eventData)
    {
        if (connection is not NpgsqlConnection) return;
        if (FinancialWriteScope.Current is { DatabaseStartedAt: null } scope)
        {
            using var capture = Capture(connection, scope); using var reader = capture.ExecuteReader(); reader.Read();
            scope.DatabaseStartedAt = reader.GetFieldValue<DateTimeOffset>(0); scope.ExpectedVersion = reader.IsDBNull(1) ? null : reader.GetInt64(1);
        }
        using var command = Command(connection); command.ExecuteNonQuery();
    }
    public override async Task ConnectionOpenedAsync(DbConnection connection, ConnectionEndEventData eventData, CancellationToken ct = default)
    {
        if (connection is not NpgsqlConnection) return;
        if (FinancialWriteScope.Current is { DatabaseStartedAt: null } scope)
        {
            await using var capture = Capture(connection, scope); await using var reader = await capture.ExecuteReaderAsync(ct); await reader.ReadAsync(ct);
            scope.DatabaseStartedAt = reader.GetFieldValue<DateTimeOffset>(0); scope.ExpectedVersion = reader.IsDBNull(1) ? null : reader.GetInt64(1);
        }
        await using var command = Command(connection); await command.ExecuteNonQueryAsync(ct);
    }
}
internal sealed class FinancialWriteTransactionInterceptor : DbTransactionInterceptor
{
    private static DbCommand? Command(DbTransaction transaction)
    {
        if (transaction.Connection is not NpgsqlConnection || FinancialWriteScope.Current is not { } scope) return null;
        var command = transaction.Connection.CreateCommand(); command.Transaction = transaction;
        command.CommandText = "SELECT set_config('saldo360.writer_started_at', @started, true), set_config('saldo360.writer_workspace_version', @version, true)";
        command.Parameters.Add(new NpgsqlParameter("started", scope.DatabaseStartedAt?.ToString("O", CultureInfo.InvariantCulture) ?? ""));
        command.Parameters.Add(new NpgsqlParameter("version", scope.ExpectedVersion?.ToString(CultureInfo.InvariantCulture) ?? ""));
        if (scope.WorkspaceId is { } id)
        {
            // Root first, before a repository takes any child row locks.
            command.CommandText += "; SELECT id FROM financial_workspaces WHERE id=@w FOR UPDATE";
            command.Parameters.Add(new NpgsqlParameter("w", id));
        }
        return command;
    }
    public override DbTransaction TransactionStarted(DbConnection connection, TransactionEndEventData eventData, DbTransaction result)
    { using var command = Command(result); command?.ExecuteNonQuery(); return result; }
    public override async ValueTask<DbTransaction> TransactionStartedAsync(DbConnection connection, TransactionEndEventData eventData, DbTransaction result, CancellationToken ct = default)
    { await using var command = Command(result); if (command is not null) await command.ExecuteNonQueryAsync(ct); return result; }
}
