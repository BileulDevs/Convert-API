namespace ConvertApi.Core.Jobs;

public interface IJobRepository
{
    Task AddAsync(ConversionJob job, CancellationToken cancellationToken);

    /// <summary>Returns the job only when it belongs to <paramref name="ownerId"/>, so other keys get a 404.</summary>
    Task<ConversionJob?> FindAsync(Guid id, Guid ownerId, CancellationToken cancellationToken);

    /// <summary>How many of the owner's jobs are still queued or running.</summary>
    Task<int> CountActiveAsync(Guid ownerId, CancellationToken cancellationToken);

    /// <summary>Succeeded jobs whose result expired at or before <paramref name="now"/>, soonest expiry first.</summary>
    Task<IReadOnlyList<ConversionJob>> ListExpiredAsync(DateTimeOffset now, int limit, CancellationToken cancellationToken);

    /// <summary>Saves a job loaded through this repository; fails if someone else changed it in the meantime.</summary>
    Task SaveAsync(ConversionJob job, CancellationToken cancellationToken);
}
