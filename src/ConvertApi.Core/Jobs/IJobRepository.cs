namespace ConvertApi.Core.Jobs;

public interface IJobRepository
{
    Task AddAsync(ConversionJob job, CancellationToken cancellationToken);

    /// <summary>Returns the job only when it belongs to <paramref name="ownerId"/>, so other keys get a 404.</summary>
    Task<ConversionJob?> FindAsync(Guid id, Guid ownerId, CancellationToken cancellationToken);
}
