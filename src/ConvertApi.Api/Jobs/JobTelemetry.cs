using System.Diagnostics;
using System.Diagnostics.Metrics;
using ConvertApi.Core.Formats;
using ConvertApi.Core.Jobs;

namespace ConvertApi.Api.Jobs;

/// <summary>The API's business metrics; the worker reports how the jobs end.</summary>
public sealed class JobTelemetry
{
    /// <summary>The API's application name, which ServiceDefaults registers as a meter.</summary>
    public const string MeterName = "ConvertApi.Api";

    private readonly Counter<long> _created;

    public JobTelemetry(IMeterFactory meters)
    {
        ArgumentNullException.ThrowIfNull(meters);

        _created = meters.Create(MeterName).CreateCounter<long>(
            "convertapi.jobs.created", "{job}", "Jobs accepted, by source format family.");
    }

    public void JobCreated(ConversionJob job)
    {
        ArgumentNullException.ThrowIfNull(job);

        _created.Add(1, new KeyValuePair<string, object?>(
            "convertapi.format.family", FormatCatalog.SourceFamilyOf(job.SourceFormat)?.ToString() ?? "Unknown"));
    }

    /// <summary>The current request's trace, for the worker to continue (see <see cref="ConversionJob.TraceParent"/>).</summary>
    public static string? CurrentTraceParent() =>
        Activity.Current is { IdFormat: ActivityIdFormat.W3C } activity ? activity.Id : null;
}
