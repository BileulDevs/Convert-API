namespace ConvertApi.Core.Jobs;

/// <summary>Thrown when a job is asked to move to a status its current one does not lead to.</summary>
public sealed class InvalidJobTransitionException : InvalidOperationException
{
    public InvalidJobTransitionException(Guid jobId, JobStatus from, JobStatus to)
        : base($"Job {jobId} cannot go from {from} to {to}.")
    {
        JobId = jobId;
        From = from;
        To = to;
    }

    public Guid JobId { get; }

    public JobStatus From { get; }

    public JobStatus To { get; }
}
