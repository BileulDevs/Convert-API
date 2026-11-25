using ConvertApi.Core.Jobs;

namespace ConvertApi.Api.Jobs;

/// <summary>What the API tells a client about one of its jobs.</summary>
public sealed record JobResponse(
    Guid Id,
    JobStatus Status,
    string SourceFormat,
    string TargetFormat,
    DateTimeOffset CreatedAt,
    DateTimeOffset? CompletedAt,
    DateTimeOffset? ExpiresAt,
    string? FailureReason)
{
    public static JobResponse From(ConversionJob job)
    {
        ArgumentNullException.ThrowIfNull(job);
        return new JobResponse(
            job.Id,
            job.Status,
            job.SourceFormat,
            job.TargetFormat,
            job.CreatedAt,
            job.CompletedAt,
            job.ExpiresAt,
            job.FailureReason);
    }
}
