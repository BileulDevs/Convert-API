using ConvertApi.Core.Jobs;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;

namespace ConvertApi.Worker;

/// <summary>
/// Claims jobs one at a time and processes them. When there is nothing to do it waits a
/// little before looking again. Run more worker instances to convert more files at once.
/// </summary>
public sealed partial class ConversionWorker(
    IServiceScopeFactory scopes,
    IOptions<ConversionOptions> options,
    TimeProvider clock,
    ILogger<ConversionWorker> logger) : BackgroundService
{
    private static readonly TimeSpan ErrorBackoff = TimeSpan.FromSeconds(5);

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        LogStarted(logger);
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                if (!await ProcessNextAsync(stoppingToken))
                {
                    await Task.Delay(options.Value.PollInterval, clock, stoppingToken);
                }
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch (DbUpdateConcurrencyException)
            {
                // Another worker reclaimed the job (its lease ran out): its outcome wins.
                LogLostRace(logger);
            }
#pragma warning disable CA1031 // One failing job must not stop the worker; it is logged and retried later.
            catch (Exception exception)
#pragma warning restore CA1031
            {
                LogProcessingFailed(logger, exception);
                await Task.Delay(ErrorBackoff, clock, stoppingToken);
            }
        }
    }

    /// <returns>Whether a job was found.</returns>
    private async Task<bool> ProcessNextAsync(CancellationToken cancellationToken)
    {
        await using var scope = scopes.CreateAsyncScope();
        var dispatcher = scope.ServiceProvider.GetRequiredService<IJobDispatcher>();
        var job = await dispatcher.ClaimNextAsync(clock.GetUtcNow(), cancellationToken);
        if (job is null)
        {
            return false;
        }

        try
        {
            await scope.ServiceProvider.GetRequiredService<JobProcessor>().ProcessAsync(job, cancellationToken);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            // The worker is stopping mid-conversion: hand the job back right away.
            await dispatcher.ReleaseAsync(job.Id, clock.GetUtcNow(), CancellationToken.None);
            LogReleased(logger, job.Id);
            throw;
        }

        return true;
    }

    [LoggerMessage(Level = LogLevel.Information, Message = "Worker stopping: job {JobId} handed back to the queue")]
    private static partial void LogReleased(ILogger logger, Guid jobId);

    [LoggerMessage(Level = LogLevel.Information, Message = "Conversion worker started")]
    private static partial void LogStarted(ILogger logger);

    [LoggerMessage(Level = LogLevel.Warning, Message = "A job was taken over by another worker; this worker's result is dropped")]
    private static partial void LogLostRace(ILogger logger);

    [LoggerMessage(Level = LogLevel.Error, Message = "Processing a job failed; it will be retried once its lease runs out")]
    private static partial void LogProcessingFailed(ILogger logger, Exception exception);
}
