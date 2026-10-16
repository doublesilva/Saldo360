using System.Diagnostics;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Npgsql;
using Saldo360.Infrastructure.Persistence;
using Testcontainers.PostgreSql;

namespace Saldo360.Tests.Support;

/// <summary>
/// PostgreSQL real (Testcontainers) compartilhado pelos testes da coleção <see cref="PostgresCollection"/>.
/// Cada consumidor usa um banco próprio, clonado de um template migrado uma vez por fixture.
/// Requer Docker.
/// </summary>
public sealed class PostgresFixture : IAsyncLifetime
{
    private readonly string _fixtureId = Guid.NewGuid().ToString("N")[..8];
    private readonly string _templateDatabase = $"saldo360_template_{Guid.NewGuid():N}";
    private readonly SemaphoreSlim _databaseLock = new(1, 1);
    // Cada consumidor cria um banco (e um pool) próprio: mais conexões que o padrão de 100.
    private readonly PostgreSqlContainer _container = new PostgreSqlBuilder("postgres:17-alpine")
        .WithCommand("-c", "max_connections=300")
        .Build();

    public async Task InitializeAsync()
    {
        var watch = Stopwatch.StartNew();
        Log("container", "início");
        using var timeout = new CancellationTokenSource(TimeSpan.FromMinutes(2));
        try
        {
            await _container.StartAsync(timeout.Token);
            Log("container", $"concluído em {watch.ElapsedMilliseconds} ms");
            await CreateAndMigrateTemplateAsync(timeout.Token);
        }
        catch (Exception exception)
        {
            Log("inicialização", $"falha: {exception}");
            throw;
        }
    }

    public async Task DisposeAsync()
    {
        Log("container", "descarte iniciado");
        await _container.DisposeAsync();
        Log("container", "descarte concluído");
    }

    /// <summary>Connection string para um banco ainda inexistente, com nome único.</summary>
    public string NewDatabaseConnectionString() =>
        new NpgsqlConnectionStringBuilder(_container.GetConnectionString())
        {
            Database = $"saldo360_{Guid.NewGuid():N}",
        }.ConnectionString;

    /// <summary>Cria o banco isolado da connection string a partir do template já migrado.</summary>
    public async Task CreateDatabaseAsync(string connectionString)
    {
        var database = new NpgsqlConnectionStringBuilder(connectionString).Database!;
        using var lockTimeout = new CancellationTokenSource(TimeSpan.FromMinutes(2));
        var lockWatch = Stopwatch.StartNew();
        await _databaseLock.WaitAsync(lockTimeout.Token);
        try
        {
            Log("lock", $"adquirido para '{database}' após {lockWatch.ElapsedMilliseconds} ms");
            Log("clone", $"início do banco '{database}'");
            NpgsqlConnection.ClearAllPools();
            await using (var admin = new NpgsqlConnection(_container.GetConnectionString()))
            {
                await admin.OpenAsync(lockTimeout.Token);
                await using var create = new NpgsqlCommand(
                    $"CREATE DATABASE \"{database}\" WITH TEMPLATE \"{_templateDatabase}\"", admin);
                await create.ExecuteNonQueryAsync(lockTimeout.Token);
            }
            Log("clone", $"banco '{database}' concluído após {lockWatch.ElapsedMilliseconds} ms");
            NpgsqlConnection.ClearAllPools();
        }
        catch (Exception exception)
        {
            Log("clone", $"falha no banco '{database}': {exception}");
            throw;
        }
        finally
        {
            _databaseLock.Release();
        }
    }

    /// <summary>Banco novo e migrado; devolve a connection string.</summary>
    public async Task<string> CreateDatabaseAsync()
    {
        var connectionString = NewDatabaseConnectionString();
        await CreateDatabaseAsync(connectionString);
        return connectionString;
    }

    /// <summary>DbContext com a mesma configuração de provedor da API (e interceptadores opcionais, para inspecionar SQL).</summary>
    public static Saldo360DbContext CreateDbContext(string connectionString, params IInterceptor[] interceptors) =>
        new((DbContextOptions<Saldo360DbContext>)Saldo360DbContext
            .Configure(new DbContextOptionsBuilder<Saldo360DbContext>(), connectionString)
            .AddInterceptors(interceptors)
            .Options);

    private async Task CreateAndMigrateTemplateAsync(CancellationToken cancellationToken)
    {
        var watch = Stopwatch.StartNew();
        var templateConnection = new NpgsqlConnectionStringBuilder(_container.GetConnectionString())
        {
            Database = _templateDatabase,
        }.ConnectionString;
        Log("template", $"criação de '{_templateDatabase}' iniciada");
        await using (var admin = new NpgsqlConnection(_container.GetConnectionString()))
        {
            await admin.OpenAsync(cancellationToken);
            await using var create = new NpgsqlCommand($"CREATE DATABASE \"{_templateDatabase}\"", admin);
            await create.ExecuteNonQueryAsync(cancellationToken);
        }
        await using (var db = CreateDbContext(templateConnection))
        {
            await db.Database.MigrateAsync(cancellationToken);
        }
        NpgsqlConnection.ClearAllPools();
        Log("template", $"migrations concluídas em {watch.ElapsedMilliseconds} ms");
    }

    private void Log(string stage, string message) =>
        Console.WriteLine($"[PostgresFixture:{_fixtureId}] {stage}: {message}");
}

[CollectionDefinition(Name)]
public sealed class PostgresCollection : ICollectionFixture<PostgresFixture>
{
    public const string Name = "postgres";
}
