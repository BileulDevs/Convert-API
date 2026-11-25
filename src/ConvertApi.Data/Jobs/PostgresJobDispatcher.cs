using ConvertApi.Core.Jobs;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;

namespace ConvertApi.Data.Jobs;

/// <summary>
/// Uses the Jobs table itself as the queue. A claim locks the oldest available row with
/// FOR UPDATE SKIP LOCKED, so concurrent workers never get the same job, and starts it in
/// the same transaction: there is no separate message to lose or to keep in sync.
/// </summary>
internal sealed class PostgresJobDispatcher(ConvertApiDbContext db, IOptions<JobDispatchOptions> options) : IJobDispatcher
{
    private readonly JobDispatchOptions _options = options.Value;

    public Task<ConversionJob?> ClaimNextAsync(DateTimeOffset now, CancellationToken cancellationToken)
    {
        // A transaction under a retrying strategy must run as one retriable unit.
        var strategy = db.Database.CreateExecutionStrategy();
        return strategy.ExecuteAsync(token => ClaimInTransactionAsync(now.ToUniversalTime(), token), cancellationToken);
    }

    public async Task SaveAsync(ConversionJob job, CancellationToken cancellationToken)
    {
        if (db.Entry(job).State == EntityState.Detached)
        {
            throw new InvalidOperationException("Only a job claimed through this dispatcher can be saved.");
        }

        await db.SaveChangesAsync(cancellationToken);
    }

    public Task ReleaseAsync(Guid jobId, DateTimeOffset now, CancellationToken cancellationToken)
    {
        // Backdating the start puts the job past its lease: the next claim takes it, and the
        // attempt it then counts replaces the one interrupted here.
        var expired = now.ToUniversalTime() - _options.LeaseTimeout - TimeSpan.FromSeconds(1);
        return db.Jobs
            .Where(job => job.Id == jobId && job.Status == JobStatus.Processing)
            .ExecuteUpdateAsync(
                set => set
                    .SetProperty(job => job.StartedAt, expired)
                    .SetProperty(job => job.Attempts, job => job.Attempts - 1),
                cancellationToken);
    }

    private async Task<ConversionJob?> ClaimInTransactionAsync(DateTimeOffset now, CancellationToken cancellationToken)
    {
        db.ChangeTracker.Clear();
        await using var transaction = await db.Database.BeginTransactionAsync(cancellationToken);

        var staleBefore = now - _options.LeaseTimeout;
        var ids = await db.Database.SqlQuery<Guid>($"""
            SELECT "Id" AS "Value" FROM "Jobs"
            WHERE "Status" = {nameof(JobStatus.Queued)}
               OR ("Status" = {nameof(JobStatus.Processing)} AND "StartedAt" < {staleBefore})
            ORDER BY "Id"
            LIMIT 1
            FOR UPDATE SKIP LOCKED
            """).ToListAsync(cancellationToken);

        if (ids.Count == 0)
        {
            await transaction.CommitAsync(cancellationToken);
            return null;
        }

        // The row is locked by this transaction until it commits.
        var job = await db.Jobs.SingleAsync(j => j.Id == ids[0], cancellationToken);
        if (job.Attempts >= _options.MaxAttempts)
        {
            job.Fail($"Gave up after {job.Attempts} attempts: the conversion never finished.", now);
        }
        else
        {
            job.Start(now);
        }

        await db.SaveChangesAsync(cancellationToken);
        await transaction.CommitAsync(cancellationToken);
        return job;
    }
}
