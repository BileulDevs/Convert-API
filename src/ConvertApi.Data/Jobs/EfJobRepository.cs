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

    private static bool IsDuplicateKey(DbUpdateException exception) =>
        exception.InnerException is PostgresException { SqlState: PostgresErrorCodes.UniqueViolation };
}
