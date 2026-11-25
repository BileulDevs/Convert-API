using System.ComponentModel.DataAnnotations;

namespace ConvertApi.Worker;

/// <summary>Bound from "Conversion".</summary>
public sealed class ConversionOptions
{
    public const string SectionName = "Conversion";

    /// <summary>How long a result stays downloadable before it is deleted (24 h by default, per the spec).</summary>
    [Range(typeof(TimeSpan), "00:01:00", "30.00:00:00")]
    public TimeSpan ResultRetention { get; set; } = TimeSpan.FromHours(24);

    /// <summary>
    /// A conversion running longer is stopped and the job fails. Must be shorter than the
    /// dispatcher's lease, so a working worker never has its job taken over.
    /// </summary>
    [Range(typeof(TimeSpan), "00:00:00.100", "1.00:00:00")]
    public TimeSpan MaxDuration { get; set; } = TimeSpan.FromMinutes(10);

    /// <summary>Largest result a conversion may produce.</summary>
    [Range(1, long.MaxValue)]
    public long MaxResultBytes { get; set; } = 2L * 1024 * 1024 * 1024;

    /// <summary>Where each conversion gets a scratch directory, removed when it ends.</summary>
    [Required]
    public string WorkRoot { get; set; } = Path.Combine(Path.GetTempPath(), "convertapi-work");

    /// <summary>How long an idle worker waits before looking for work again.</summary>
    [Range(typeof(TimeSpan), "00:00:00.100", "00:01:00")]
    public TimeSpan PollInterval { get; set; } = TimeSpan.FromSeconds(1);
}
