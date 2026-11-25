using ConvertApi.Core.Jobs;
using ConvertApi.Data.Jobs;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Diagnostics.HealthChecks;
using Microsoft.Extensions.Hosting;

namespace ConvertApi.Data;

public static class DataServices
{
    /// <summary>Connection string name, matching the database resource in the Aspire AppHost.</summary>
    public const string ConnectionName = "convertapi";

    private const string MigrateOnStartupKey = "Database:MigrateOnStartup";
    private const string DatabaseHealthCheck = "database";
    private const int MaxRetries = 3;
    private static readonly TimeSpan MaxRetryDelay = TimeSpan.FromSeconds(2);
    private static readonly TimeSpan HealthCheckTimeout = TimeSpan.FromSeconds(5);

    public static TBuilder AddConvertApiData<TBuilder>(this TBuilder builder)
        where TBuilder : IHostApplicationBuilder
    {
        var connectionString = builder.Configuration.GetConnectionString(ConnectionName);
        builder.Services.AddDbContext<ConvertApiDbContext>(options => options.UseNpgsql(
            connectionString,
            // A few quick retries for transient failures; the defaults (6 retries, up to 30 s)
            // would hold an API request far too long.
            npgsql => npgsql.EnableRetryOnFailure(MaxRetries, MaxRetryDelay, errorCodesToAdd: null)));
        builder.Services.AddScoped<IJobRepository, EfJobRepository>();

        // Readiness fails while the database is unreachable, and fast: connection retries
        // would otherwise keep the probe waiting far longer than the host's probe timeout.
        builder.Services.AddHealthChecks().AddDbContextCheck<ConvertApiDbContext>(DatabaseHealthCheck);
        builder.Services.Configure<HealthCheckServiceOptions>(options =>
            options.Registrations.Single(check => check.Name == DatabaseHealthCheck).Timeout = HealthCheckTimeout);

        if (builder.Configuration.GetValue<bool>(MigrateOnStartupKey))
        {
            builder.Services.AddHostedService<DatabaseMigrator>();
        }

        return builder;
    }
}
