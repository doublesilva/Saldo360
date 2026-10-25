using Npgsql;
using Saldo360.Tests.Support;

namespace Saldo360.Tests.Persistence;

[Collection(PostgresCollection.Name)]
[Trait("Category", "INFRA")]
public sealed class PostgresFixtureReliabilityTests(PostgresFixture postgres) : IAsyncLifetime
{
    private readonly string _databaseA = postgres.NewDatabaseConnectionString();
    private readonly string _databaseB = postgres.NewDatabaseConnectionString();

    public async Task InitializeAsync() =>
        await Task.WhenAll(postgres.CreateDatabaseAsync(_databaseA), postgres.CreateDatabaseAsync(_databaseB));

    public Task DisposeAsync() => Task.CompletedTask;

    [Fact]
    public async Task ConcurrentClones_HaveMigrationsAndRemainIsolated()
    {
        await using var first = new NpgsqlConnection(_databaseA);
        await using var second = new NpgsqlConnection(_databaseB);
        await first.OpenAsync();
        await second.OpenAsync();

        await using (var marker = new NpgsqlCommand("CREATE TABLE fixture_isolation_marker(id integer PRIMARY KEY)", first))
        {
            await marker.ExecuteNonQueryAsync();
        }

        await using var firstMigrations = new NpgsqlCommand("SELECT count(*) FROM \"__EFMigrationsHistory\"", first);
        await using var secondMigrations = new NpgsqlCommand("SELECT count(*) FROM \"__EFMigrationsHistory\"", second);
        await using var markerInSecond = new NpgsqlCommand("SELECT to_regclass('public.fixture_isolation_marker') IS NOT NULL", second);

        var countA = (long)(await firstMigrations.ExecuteScalarAsync())!;
        var countB = (long)(await secondMigrations.ExecuteScalarAsync())!;
        var secondHasMarker = (bool)(await markerInSecond.ExecuteScalarAsync())!;

        Assert.True(countA > 0);
        Assert.Equal(countA, countB);
        Assert.False(secondHasMarker);
    }
}
