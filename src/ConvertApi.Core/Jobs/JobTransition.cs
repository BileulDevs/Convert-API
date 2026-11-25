namespace ConvertApi.Core.Jobs;

/// <summary>One recorded status change of a job.</summary>
public sealed record JobTransition(JobStatus From, JobStatus To, DateTimeOffset At);
