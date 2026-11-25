using System.Diagnostics;
using System.Diagnostics.Metrics;
using ConvertApi.Core.Formats;
using ConvertApi.Core.Jobs;

namespace ConvertApi.Worker;

/// <summary>
/// What the worker reports through OpenTelemetry: one span per conversion attempt, inside the trace
/// of the request that created the job, and the business metrics: jobs a worker finished, by status
/// and format family (failure rate = failed / all; jobs canceled before any worker took them are
/// not in it), processing time (download, conversion and storage) and the queue length.
/// </summary>
/// <remarks>
/// The conversion is a child of the request rather than a linked trace, so one trace tells the
/// whole story; it lasts as long as the queue wait, which is short here. Webhook deliveries keep
/// traces of their own. Every worker reports the same queue length: aggregate it with max, not sum.
/// </remarks>
public sealed class ConversionTelemetry
{
    /// <summary>Also the worker's application name, which ServiceDefaults registers as a trace source.</summary>
    public const string SourceName = "ConvertApi.Worker";

    public const string StatusTag = "convertapi.job.status";
    public const string FamilyTag = "convertapi.format.family";

    private static readonly ActivitySource Source = new(SourceName);

    private readonly Counter<long> _completed;
    private readonly Histogram<double> _duration;
    private const int NotSampledYet = -1;
    private int _queued = NotSampledYet;

    public ConversionTelemetry(IMeterFactory meters)
    {
        ArgumentNullException.ThrowIfNull(meters);

        var meter = meters.Create(SourceName);
        _completed = meter.CreateCounter<long>(
            "convertapi.jobs.completed", "{job}", "Jobs that reached a final status, by status and format family.");
        _duration = meter.CreateHistogram<double>(
            "convertapi.conversion.duration", "s", "Time to process one job (download, conversion, storage), by outcome and format family.");

        // Nothing until the first sample: a 0 would read as an empty queue.
        meter.CreateObservableGauge(
            "convertapi.jobs.queued",
            () => Volatile.Read(ref _queued) is var queued and >= 0 ? [new Measurement<int>(queued)] : Array.Empty<Measurement<int>>(),
            "{job}",
            "Jobs waiting for a worker.");
    }

    /// <summary>Starts the conversion's trace as a child of the creating request's, when the job kept one.</summary>
    public static Activity? StartConversion(ConversionJob job)
    {
        ArgumentNullException.ThrowIfNull(job);

        ActivityContext.TryParse(job.TraceParent, traceState: null, out var parent);
        var activity = Source.StartActivity("convert job", ActivityKind.Consumer, parent);
        activity?.SetTag("convertapi.job.id", job.Id.ToString());
        activity?.SetTag("convertapi.format.source", job.SourceFormat);
        activity?.SetTag("convertapi.format.target", job.TargetFormat);
        activity?.SetTag("convertapi.job.attempt", job.Attempts);
        return activity;
    }

    /// <param name="conversionTime">How long the conversion ran, when one ran at all.</param>
    public void JobFinished(ConversionJob job, JobStatus status, TimeSpan? conversionTime)
    {
        ArgumentNullException.ThrowIfNull(job);

        var tags = new TagList
        {
            { StatusTag, status.ToString() },
            { FamilyTag, FormatCatalog.SourceFamilyOf(job.SourceFormat)?.ToString() ?? "Unknown" },
        };
        _completed.Add(1, tags);
        if (conversionTime is { } time)
        {
            _duration.Record(time.TotalSeconds, tags);
        }
    }

    public void SetQueueLength(int queued) => Volatile.Write(ref _queued, queued);
}
