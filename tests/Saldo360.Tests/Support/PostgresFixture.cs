using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Npgsql;
using Saldo360.Infrastructure.Persistence;
using Testcontainers.PostgreSql;

namespace Saldo360.Tests.Support;

/// <summary>
/// PostgreSQL real (Testcontainers) compartilhado pelos testes da coleção <see cref="PostgresCollection"/>.
/// Cada teste usa um banco próprio, criado e migrado com as migrations do projeto.
/// Requer Docker.
/// </summary>
public sealed class PostgresFixture : IAsyncLifetime
{
    // Cada teste cria um banco (e um pool) próprio: mais conexões que o padrão de 100.
    private readonly PostgreSqlContainer _container = new PostgreSqlBuilder("postgres:17-alpine")
        .WithCommand("-c", "max_connections=300")
        .Build();

    public Task InitializeAsync() => _container.StartAsync();

    public Task DisposeAsync() => _container.DisposeAsync().AsTask();

    /// <summary>Connection string para um banco ainda inexistente, com nome único.</summary>
    public string NewDatabaseConnectionString() =>
        new NpgsqlConnectionStringBuilder(_container.GetConnectionString())
        {
            Database = $"saldo360_{Guid.NewGuid():N}",
        }.ConnectionString;

    /// <summary>Cria o banco da connection string e aplica todas as migrations.</summary>
    public async Task CreateDatabaseAsync(string connectionString)
    {
        // Os testes da coleção rodam em série: fecha as conexões ociosas dos bancos anteriores.
        NpgsqlConnection.ClearAllPools();

        var database = new NpgsqlConnectionStringBuilder(connectionString).Database!;
        await using (var admin = new NpgsqlConnection(_container.GetConnectionString()))
        {
            await admin.OpenAsync();
            await using var create = new NpgsqlCommand($"CREATE DATABASE \"{database}\"", admin);
            await create.ExecuteNonQueryAsync();
        }

        await using var db = CreateDbContext(connectionString);
        await db.Database.MigrateAsync();
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
}

[CollectionDefinition(Name)]
public sealed class PostgresCollection : ICollectionFixture<PostgresFixture>
{
    public const string Name = "postgres";
}
