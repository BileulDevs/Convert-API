namespace ConvertApi.Core.Jobs;

/// <summary>The allowed moves between job statuses.</summary>
public static class JobStateMachine
{
    /// <remarks>
    /// Processing → Processing is allowed on purpose: when a worker crashes, the queue
    /// redelivers the message and another worker picks the job up again.
    /// </remarks>
    public static bool CanTransition(JobStatus from, JobStatus to) => (from, to) switch
    {
        (JobStatus.Queued, JobStatus.Processing or JobStatus.Canceled or JobStatus.Failed) => true,
        (JobStatus.Processing, JobStatus.Processing or JobStatus.Succeeded or JobStatus.Failed or JobStatus.Canceled) => true,
        (JobStatus.Succeeded, JobStatus.Expired) => true,
        _ => false,
    };

    /// <summary>A terminal status has no way out.</summary>
    public static bool IsTerminal(JobStatus status) =>
        status is JobStatus.Failed or JobStatus.Canceled or JobStatus.Expired;
}
