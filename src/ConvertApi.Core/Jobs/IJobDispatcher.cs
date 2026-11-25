namespace ConvertApi.Core.Jobs;

/// <summary>Hands jobs out to workers, one worker per job (the job queue).</summary>
public interface IJobDispatcher
{
    /// <summary>
    /// Takes the oldest job waiting for a worker (or one whose worker stopped answering) and
    /// starts it. A job that already used all its attempts comes back as Failed instead, so the
    /// caller can clean it up. Returns null when there is nothing to do.
    /// </summary>
    Task<ConversionJob?> ClaimNextAsync(DateTimeOffset now, CancellationToken cancellationToken);

    /// <summary>Saves a claimed job; fails if another worker changed it in the meantime.</summary>
    Task SaveAsync(ConversionJob job, CancellationToken cancellationToken);

    /// <summary>
    /// Gives a running job back when its worker stops (a deployment, a scale-in): it can be
    /// claimed again at once instead of after the lease, and the interrupted start does not
    /// count as an attempt. Does nothing if the job is no longer running.
    /// </summary>
    Task ReleaseAsync(Guid jobId, DateTimeOffset now, CancellationToken cancellationToken);

    /// <summary>Whether the job's owner canceled it; read fresh from the store, never from a cached copy.</summary>
    Task<bool> IsCanceledAsync(Guid jobId, CancellationToken cancellationToken);

    /// <summary>How many jobs wait for a worker: the queue length.</summary>
    Task<int> CountQueuedAsync(CancellationToken cancellationToken);
}
