namespace ConvertApi.Core.Jobs;

public interface IJobRepository
{
    Task AddAsync(ConversionJob job, CancellationToken cancellationToken);

    /// <summary>Returns the job only when it belongs to <paramref name="ownerId"/>, so other keys get a 404.</summary>
    Task<ConversionJob?> FindAsync(Guid id, Guid ownerId, CancellationToken cancellationToken);

    /// <summary>
    /// Like <see cref="FindAsync"/>, but reads the job as stored right now on every call, so repeated
    /// calls see other processes' changes. The copy is not tracked: it cannot be saved.
    /// </summary>
    Task<ConversionJob?> ReadLatestAsync(Guid id, Guid ownerId, CancellationToken cancellationToken);

    /// <summary>How many of the owner's jobs are still queued or running.</summary>
    Task<int> CountActiveAsync(Guid ownerId, CancellationToken cancellationToken);

    /// <summary>Succeeded jobs whose result expired at or before <paramref name="now"/>, soonest expiry first.</summary>
    Task<IReadOnlyList<ConversionJob>> ListExpiredAsync(DateTimeOffset now, int limit, CancellationToken cancellationToken);

    /// <summary>
    /// Claims up to <paramref name="limit"/> jobs whose callback is due, each for one sender: concurrent
    /// senders never get the same job. A claimed job comes back once <paramref name="lease"/> runs out.
    /// </summary>
    Task<IReadOnlyList<ConversionJob>> ClaimDueNotificationsAsync(
        DateTimeOffset now, TimeSpan lease, int limit, CancellationToken cancellationToken);

    /// <summary>Saves a job loaded through this repository; fails if someone else changed it in the meantime.</summary>
    Task SaveAsync(ConversionJob job, CancellationToken cancellationToken);
}
