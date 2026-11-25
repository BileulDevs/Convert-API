using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;

namespace ConvertApi.Worker;

/// <summary>
/// Runs <see cref="ResultSweeper"/> every <see cref="ConversionOptions.ResultSweepInterval"/>,
/// and again straight away while it finds full batches. Every worker instance runs one.
/// </summary>
public sealed partial class ResultSweepWorker(
    IServiceScopeFactory scopes,
    IOptions<ConversionOptions> options,
    TimeProvider clock,
    ILogger<ResultSweepWorker> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                if (await SweepOnceAsync(stoppingToken) < ResultSweeper.BatchSize)
                {
                    await Task.Delay(options.Value.ResultSweepInterval, clock, stoppingToken);
                }
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch (DbUpdateConcurrencyException)
            {
                // Another worker's sweep expired the same job first. Sweep again at once, in a
                // new scope: that job is no longer listed, so each retry makes progress.
                LogLostRace(logger);
            }
#pragma warning disable CA1031 // A failing sweep must not stop the worker; it is logged and tried again later.
            catch (Exception exception)
#pragma warning restore CA1031
            {
                LogSweepFailed(logger, exception);
                await Task.Delay(options.Value.ResultSweepInterval, clock, stoppingToken);
            }
        }
    }

    private async Task<int> SweepOnceAsync(CancellationToken cancellationToken)
    {
        // A fresh scope per sweep: the DbContext never accumulates tracked jobs.
        await using var scope = scopes.CreateAsyncScope();
        return await scope.ServiceProvider.GetRequiredService<ResultSweeper>().SweepAsync(cancellationToken);
    }

    [LoggerMessage(Level = LogLevel.Information, Message = "Another worker expired a job during this sweep; sweeping again")]
    private static partial void LogLostRace(ILogger logger);

    [LoggerMessage(Level = LogLevel.Error, Message = "Sweeping expired results failed; trying again later")]
    private static partial void LogSweepFailed(ILogger logger, Exception exception);
}
