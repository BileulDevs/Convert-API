using System.ComponentModel.DataAnnotations;

namespace ConvertApi.Data.Jobs;

/// <summary>Bound from "Jobs:Dispatch".</summary>
public sealed class JobDispatchOptions
{
    public const string SectionName = "Jobs:Dispatch";

    /// <summary>
    /// How long a worker owns a job. A job still processing after that is presumed abandoned
    /// (the worker crashed) and handed to another worker. Longer than any conversion may take.
    /// </summary>
    [Range(typeof(TimeSpan), "00:00:01", "1.00:00:00")]
    public TimeSpan LeaseTimeout { get; set; } = TimeSpan.FromMinutes(15);

    /// <summary>After this many starts, a job that keeps getting abandoned is failed.</summary>
    [Range(1, 20)]
    public int MaxAttempts { get; set; } = 3;
}
