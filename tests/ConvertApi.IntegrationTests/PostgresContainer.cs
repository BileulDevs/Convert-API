using Npgsql;
using Testcontainers.PostgreSql;

namespace ConvertApi.IntegrationTests;

/// <summary>
/// One PostgreSQL container for the whole test run; every fixture gets its own database in it,
/// so test classes running in parallel never share or race on a schema.
/// Testcontainers removes the container when the test process exits.
/// </summary>
internal static class PostgresContainer
{
    private static readonly Lazy<Task<PostgreSqlContainer>> Container = new(StartAsync);

    public static async Task<string> NewDatabaseConnectionStringAsync()
    {
        var container = await Container.Value;
        return new NpgsqlConnectionStringBuilder(container.GetConnectionString())
        {
            Database = $"convertapi_{Guid.NewGuid():N}",

            // Every fixture has its own pool on one server: keep them small.
            MaxPoolSize = 10,
        }.ConnectionString;
    }

    private static async Task<PostgreSqlContainer> StartAsync()
    {
        var container = new PostgreSqlBuilder("postgres:17-alpine").Build();
        await container.StartAsync();
        return container;
    }
}
