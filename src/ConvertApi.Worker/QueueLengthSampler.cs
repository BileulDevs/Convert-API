using ConvertApi.Core.Jobs;

namespace ConvertApi.Worker;

/// <summary>
/// Refreshes the convertapi.jobs.queued gauge. Metrics are read synchronously by the exporter, so
/// the database is asked here, every <see cref="Interval"/>, rather than on each read.
/// </summary>
public sealed partial class QueueLengthSampler(
    IServiceScopeFactory scopes,
    ConversionTelemetry telemetry,
    TimeProvider clock,
    ILogger<QueueLengthSampler> logger) : BackgroundService
{
    private static readonly TimeSpan Interval = TimeSpan.FromSeconds(15);

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                await using var scope = scopes.CreateAsyncScope();
                var dispatcher = scope.ServiceProvider.GetRequiredService<IJobDispatcher>();
                telemetry.SetQueueLength(await dispatcher.CountQueuedAsync(stoppingToken));
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
#pragma warning disable CA1031 // A missed sample only leaves the gauge at its last value.
            catch (Exception exception)
#pragma warning restore CA1031
            {
                LogSampleFailed(logger, exception);
            }

            await Task.Delay(Interval, clock, stoppingToken);
        }
    }

    [LoggerMessage(Level = LogLevel.Warning, Message = "Could not read the queue length; the gauge keeps its last value")]
    private static partial void LogSampleFailed(ILogger logger, Exception exception);
}
