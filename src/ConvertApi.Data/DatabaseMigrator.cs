using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace ConvertApi.Data;

/// <summary>
/// Applies pending migrations while the host starts, before it serves requests.
/// Enabled for local runs and tests; production applies migrations as a release step.
/// </summary>
internal sealed partial class DatabaseMigrator(IServiceScopeFactory scopes, ILogger<DatabaseMigrator> logger) : IHostedService
{
    public async Task StartAsync(CancellationToken cancellationToken)
    {
        await using var scope = scopes.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<ConvertApiDbContext>();
        await db.Database.MigrateAsync(cancellationToken);
        LogMigrated(logger);
    }

    public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;

    [LoggerMessage(Level = LogLevel.Information, Message = "Database schema is up to date")]
    private static partial void LogMigrated(ILogger logger);
}
