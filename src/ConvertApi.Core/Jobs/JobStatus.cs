namespace ConvertApi.Core.Jobs;

/// <summary>Where a conversion job stands. Transitions are governed by <see cref="JobStateMachine"/>.</summary>
public enum JobStatus
{
    Queued,
    Processing,
    Succeeded,
    Failed,
    Canceled,
    Expired,
}
