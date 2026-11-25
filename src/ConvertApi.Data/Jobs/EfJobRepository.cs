using ConvertApi.Core.Jobs;
using Microsoft.EntityFrameworkCore;
using Npgsql;

namespace ConvertApi.Data.Jobs;

internal sealed class EfJobRepository(ConvertApiDbContext db) : IJobRepository
{
    public async Task AddAsync(ConversionJob job, CancellationToken cancellationToken)
    {
        db.Jobs.Add(job);
        try
        {
            await db.SaveChangesAsync(cancellationToken);
        }
        catch (DbUpdateException exception) when (IsDuplicateKey(exception))
        {
            // Ids are generated before the insert, so a duplicate means this very job was
            // already stored, typically by an attempt whose acknowledgement was lost.
            db.Entry(job).State = EntityState.Unchanged;
        }
    }

    public Task<ConversionJob?> FindAsync(Guid id, Guid ownerId, CancellationToken cancellationToken) =>
        db.Jobs.FirstOrDefaultAsync(job => job.Id == id && job.OwnerId == ownerId, cancellationToken);

    public Task<ConversionJob?> ReadLatestAsync(Guid id, Guid ownerId, CancellationToken cancellationToken) =>
        db.Jobs.AsNoTracking().FirstOrDefaultAsync(job => job.Id == id && job.OwnerId == ownerId, cancellationToken);

    public Task<int> CountActiveAsync(Guid ownerId, CancellationToken cancellationToken) =>
        db.Jobs.CountAsync(
            job => job.OwnerId == ownerId && (job.Status == JobStatus.Queued || job.Status == JobStatus.Processing),
            cancellationToken);

    public async Task<IReadOnlyList<ConversionJob>> ListExpiredAsync(
        DateTimeOffset now, int limit, CancellationToken cancellationToken)
    {
        var utcNow = now.ToUniversalTime();

        // Matches the partial index on ExpiresAt, which only holds succeeded jobs.
        return await db.Jobs
            .Where(job => job.Status == JobStatus.Succeeded && job.ExpiresAt <= utcNow)
            .OrderBy(job => job.ExpiresAt)
            .Take(limit)
            .ToListAsync(cancellationToken);
    }

    public Task<IReadOnlyList<ConversionJob>> ClaimDueNotificationsAsync(
        DateTimeOffset now, TimeSpan lease, int limit, CancellationToken cancellationToken)
    {
        // A transaction under a retrying strategy must run as one retriable unit.
        var strategy = db.Database.CreateExecutionStrategy();
        return strategy.ExecuteAsync(
            token => ClaimNotificationsInTransactionAsync(now.ToUniversalTime(), lease, limit, token), cancellationToken);
    }

    private async Task<IReadOnlyList<ConversionJob>> ClaimNotificationsInTransactionAsync(
        DateTimeOffset now, TimeSpan lease, int limit, CancellationToken cancellationToken)
    {
        db.ChangeTracker.Clear();
        await using var transaction = await db.Database.BeginTransactionAsync(cancellationToken);

        // The rows stay locked until the commit, after their lease is saved: SKIP LOCKED keeps
        // another sender from picking them meanwhile, the lease keeps it away afterwards.
        var ids = await db.Database.SqlQuery<Guid>($"""
            SELECT "Id" AS "Value" FROM "Jobs"
            WHERE "NotificationDueAt" <= {now}
            ORDER BY "NotificationDueAt"
            LIMIT {limit}
            FOR UPDATE SKIP LOCKED
            """).ToListAsync(cancellationToken);

        var jobs = await db.Jobs.Where(job => ids.Contains(job.Id)).ToListAsync(cancellationToken);
        foreach (var job in jobs)
        {
            job.ClaimNotification(now + lease);
        }

        await db.SaveChangesAsync(cancellationToken);
        await transaction.CommitAsync(cancellationToken);
        return jobs;
    }

    public async Task SaveAsync(ConversionJob job, CancellationToken cancellationToken)
    {
        if (db.Entry(job).State == EntityState.Detached)
        {
            throw new InvalidOperationException("Only a job loaded through this repository can be saved.");
        }

        try
        {
            await db.SaveChangesAsync(cancellationToken);
        }
        catch (DbUpdateConcurrencyException)
        {
            // Stop tracking the stale copy, so the next save in this scope does not trip on it again.
            db.Entry(job).State = EntityState.Detached;
            throw;
        }
    }

    private static bool IsDuplicateKey(DbUpdateException exception) =>
        exception.InnerException is PostgresException { SqlState: PostgresErrorCodes.UniqueViolation };
}
