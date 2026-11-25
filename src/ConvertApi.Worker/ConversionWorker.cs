namespace ConvertApi.Worker;

/// <summary>
/// Consumes conversion jobs from the queue. The queue is not wired yet:
/// for now the worker only announces that it is up.
/// </summary>
public sealed partial class ConversionWorker(ILogger<ConversionWorker> logger) : BackgroundService
{
    protected override Task ExecuteAsync(CancellationToken stoppingToken)
    {
        LogStarted(logger);
        return Task.CompletedTask;
    }

    [LoggerMessage(Level = LogLevel.Information, Message = "Conversion worker started, waiting for the job queue")]
    private static partial void LogStarted(ILogger logger);
}
