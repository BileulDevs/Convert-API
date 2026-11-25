using System.Collections.Concurrent;
using ConvertApi.Core.Jobs;

namespace ConvertApi.Api.Jobs;

/// <summary>
/// Keeps jobs in process memory. Temporary: it is replaced by the EF Core store,
/// which the worker can also read.
/// </summary>
internal sealed class InMemoryJobRepository : IJobRepository
{
    private readonly ConcurrentDictionary<Guid, ConversionJob> _jobs = new();

    public Task AddAsync(ConversionJob job, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(job);
        if (!_jobs.TryAdd(job.Id, job))
        {
            throw new InvalidOperationException($"Job {job.Id} already exists.");
        }

        return Task.CompletedTask;
    }

    public Task<ConversionJob?> FindAsync(Guid id, Guid ownerId, CancellationToken cancellationToken) =>
        Task.FromResult(_jobs.TryGetValue(id, out var job) && job.OwnerId == ownerId ? job : null);
}
