using ConvertApi.Core.Jobs;
using ConvertApi.Core.Storage;

namespace ConvertApi.Worker;

/// <summary>
/// Deletes results whose retention is over and marks their jobs Expired. Sources need no sweep:
/// <see cref="JobProcessor"/> deletes each one as soon as its job finishes.
/// </summary>
public sealed partial class ResultSweeper(
    IJobRepository jobs,
    IFileStorage storage,
    TimeProvider clock,
    ILogger<ResultSweeper> logger)
{
    /// <summary>Jobs handled per sweep, so one sweep never loads an unbounded list.</summary>
    public const int BatchSize = 100;

    /// <returns>How many jobs expired; <see cref="BatchSize"/> means more may be waiting.</returns>
    public async Task<int> SweepAsync(CancellationToken cancellationToken)
    {
        var now = clock.GetUtcNow();
        var expired = 0;
        foreach (var job in await jobs.ListExpiredAsync(now, BatchSize, cancellationToken))
        {
            try
            {
                // File first: if the save below fails, the next sweep finds the job again,
                // and deleting a file that is already gone does nothing.
                await storage.DeleteAsync(StorageKeys.ResultOf(job.Id), cancellationToken);
            }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
            {
                // The job stays Succeeded and comes back on the next sweep.
                // shortcut: a full batch of undeletable files would hide the jobs behind it; add a cursor if that ever happens.
                LogDeleteFailed(logger, job.Id, exception);
                continue;
            }

            job.Expire(now);
            await jobs.SaveAsync(job, cancellationToken);
            expired++;
        }

        if (expired > 0)
        {
            LogSwept(logger, expired);
        }

        return expired;
    }

    [LoggerMessage(Level = LogLevel.Information, Message = "Deleted {Count} expired results")]
    private static partial void LogSwept(ILogger logger, int count);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Could not delete the expired result of job {JobId}; retrying on the next sweep")]
    private static partial void LogDeleteFailed(ILogger logger, Guid jobId, Exception exception);
}
