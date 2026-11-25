using System.Diagnostics;
using System.Diagnostics.Metrics;
using ConvertApi.Core.Conversion;
using ConvertApi.Core.Jobs;
using ConvertApi.Core.Storage;
using ConvertApi.Worker;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace ConvertApi.UnitTests.Worker;

public sealed class JobProcessorTests : IDisposable
{
    private static readonly DateTimeOffset T0 = new(2026, 11, 25, 10, 0, 0, TimeSpan.Zero);
    private static readonly byte[] Source = [1, 2, 3];

    private readonly string _root = Path.Combine(Path.GetTempPath(), "convertapi-tests", Guid.NewGuid().ToString("N"));
    private readonly LocalFileStorage _storage;
    private readonly RecordingDispatcher _dispatcher = new();
    private readonly ConversionOptions _options;

    // A meter factory of this test's own: parallel tests record into other factories' meters.
    private readonly ServiceProvider _meterServices = new ServiceCollection().AddMetrics().BuildServiceProvider();
    private readonly IMeterFactory _meters;
    private readonly ConversionTelemetry _telemetry;

    public JobProcessorTests()
    {
        _storage = new LocalFileStorage(Path.Combine(_root, "storage"));
        _options = new ConversionOptions { WorkRoot = Path.Combine(_root, "work"), ResultRetention = TimeSpan.FromHours(24) };
        _meters = _meterServices.GetRequiredService<IMeterFactory>();
        _telemetry = new ConversionTelemetry(_meters);
    }

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    public void Dispose()
    {
        _meterServices.Dispose();
        if (Directory.Exists(_root))
        {
            Directory.Delete(_root, recursive: true);
        }
    }

    [Fact]
    public async Task AConvertedJob_StoresItsResultAndSucceeds()
    {
        var job = await ClaimedJobAsync();
        var converter = new FakeConverter(output: [9, 8, 7]);

        await ProcessorWith(converter).ProcessAsync(job, Ct);

        Assert.Equal(JobStatus.Succeeded, job.Status);
        Assert.Equal(T0.AddHours(24), job.ExpiresAt);
        Assert.Equal([9, 8, 7], await ReadAsync(StorageKeys.ResultOf(job.Id)));
        Assert.Same(job, Assert.Single(_dispatcher.Saved));
    }

    [Fact]
    public async Task TheConverter_GetsTheSourceFileAndTheFormats()
    {
        var job = await ClaimedJobAsync();
        var converter = new FakeConverter(output: [0]);

        await ProcessorWith(converter).ProcessAsync(job, Ct);

        Assert.Equal(Source, converter.ReceivedInput);
        Assert.Equal(("png", "jpg"), converter.ReceivedFormats);
        Assert.EndsWith(".png", converter.ReceivedInputPath, StringComparison.Ordinal);
        Assert.EndsWith(".jpg", converter.ReceivedOutputPath, StringComparison.Ordinal);
    }

    [Fact]
    public async Task AFinishedJob_NoLongerKeepsItsSourceOrWorkFiles()
    {
        var job = await ClaimedJobAsync();

        await ProcessorWith(new FakeConverter(output: [0])).ProcessAsync(job, Ct);

        Assert.False(await ExistsAsync(job.SourceFileKey));
        Assert.Empty(Directory.Exists(_options.WorkRoot) ? Directory.GetFileSystemEntries(_options.WorkRoot) : []);
    }

    [Fact]
    public async Task WithoutAConverter_TheJobFailsAndItsSourceIsDeleted()
    {
        var job = await ClaimedJobAsync();

        await ProcessorWith().ProcessAsync(job, Ct);

        Assert.Equal(JobStatus.Failed, job.Status);
        Assert.Contains("png", job.FailureReason, StringComparison.Ordinal);
        Assert.Contains("jpg", job.FailureReason, StringComparison.Ordinal);
        Assert.False(await ExistsAsync(job.SourceFileKey));
        Assert.Single(_dispatcher.Saved);
    }

    [Fact]
    public async Task ARejectedConversion_FailsTheJobWithTheConvertersReason()
    {
        var job = await ClaimedJobAsync();
        var converter = new FakeConverter(failure: new ConversionFailedException("The image is corrupt."));

        await ProcessorWith(converter).ProcessAsync(job, Ct);

        Assert.Equal(JobStatus.Failed, job.Status);
        Assert.Equal("The image is corrupt.", job.FailureReason);
        Assert.False(await ExistsAsync(StorageKeys.ResultOf(job.Id)));
        Assert.False(await ExistsAsync(job.SourceFileKey));
    }

    [Fact]
    public async Task AnUnexpectedError_LeavesTheJobRunningAndKeepsTheSource_SoItCanBeRetried()
    {
        var job = await ClaimedJobAsync();
        var converter = new FakeConverter(failure: new IOException("Disk full."));

        await Assert.ThrowsAsync<IOException>(() => ProcessorWith(converter).ProcessAsync(job, Ct));

        Assert.Equal(JobStatus.Processing, job.Status);
        Assert.Empty(_dispatcher.Saved);
        Assert.True(await ExistsAsync(job.SourceFileKey));
    }

    [Fact]
    public async Task AConversionThatRunsTooLong_IsStoppedAndFailsTheJob()
    {
        var job = await ClaimedJobAsync();
        _options.MaxDuration = TimeSpan.FromMilliseconds(200);
        var converter = new FakeConverter(output: [0], delay: TimeSpan.FromSeconds(30));

        await ProcessorWith(converter).ProcessAsync(job, Ct);

        Assert.Equal(JobStatus.Failed, job.Status);
        Assert.Contains("took longer than", job.FailureReason, StringComparison.Ordinal);
        Assert.False(await ExistsAsync(job.SourceFileKey));
    }

    [Fact]
    public async Task AMissingSourceFile_FailsTheJobInsteadOfRetryingForever()
    {
        var job = await ClaimedJobAsync();
        await _storage.DeleteAsync(job.SourceFileKey, Ct);

        await ProcessorWith(new FakeConverter(output: [0])).ProcessAsync(job, Ct);

        Assert.Equal(JobStatus.Failed, job.Status);
        Assert.Contains("no longer available", job.FailureReason, StringComparison.Ordinal);
    }

    [Fact]
    public async Task AFailedJob_DropsAnyResultLeftByAnEarlierAttempt()
    {
        var job = await ClaimedJobAsync();
        using var stale = new MemoryStream([4]);
        await _storage.SaveAsync(StorageKeys.ResultOf(job.Id), stale, maxBytes: 10, Ct);

        await ProcessorWith().ProcessAsync(job, Ct);

        Assert.Equal(JobStatus.Failed, job.Status);
        Assert.False(await ExistsAsync(StorageKeys.ResultOf(job.Id)));
    }

    [Fact]
    public async Task AJobAlreadyFailedByTheDispatcher_IsOnlyCleanedUp()
    {
        var job = await ClaimedJobAsync();
        job.Fail("Gave up after 3 attempts.", T0);
        var converter = new FakeConverter(output: [0]);

        await ProcessorWith(converter).ProcessAsync(job, Ct);

        Assert.Null(converter.ReceivedFormats);
        Assert.Empty(_dispatcher.Saved);
        Assert.False(await ExistsAsync(job.SourceFileKey));
    }

    [Fact]
    public async Task AJobCanceledByItsOwnerMidConversion_StopsTheConverterAndLeavesNoFiles()
    {
        var job = await ClaimedJobAsync();
        _options.CancellationCheckInterval = TimeSpan.FromMilliseconds(50);
        _dispatcher.Canceled = true;
        var converter = new FakeConverter(output: [0], delay: TimeSpan.FromMinutes(5));

        var processing = ProcessorWith(converter).ProcessAsync(job, Ct);

        await processing.WaitAsync(TimeSpan.FromSeconds(10), Ct);
        Assert.Empty(_dispatcher.Saved);
        Assert.False(await ExistsAsync(job.SourceFileKey));
        Assert.False(await ExistsAsync(StorageKeys.ResultOf(job.Id)));
    }

    [Fact]
    public async Task AFailedCancelCheck_IsRetried_SoALaterCancelStillStopsTheConversion()
    {
        var job = await ClaimedJobAsync();
        _options.CancellationCheckInterval = TimeSpan.FromMilliseconds(50);
        _dispatcher.Canceled = true;
        _dispatcher.FailingCancelChecks = 2;
        var converter = new FakeConverter(output: [0], delay: TimeSpan.FromMinutes(5));

        await ProcessorWith(converter).ProcessAsync(job, Ct).WaitAsync(TimeSpan.FromSeconds(10), Ct);

        Assert.Empty(_dispatcher.Saved);
        Assert.False(await ExistsAsync(job.SourceFileKey));
    }

    [Fact]
    public async Task AJobCanceledJustAsItSucceeded_DropsItsResult()
    {
        var job = await ClaimedJobAsync();
        _dispatcher.Canceled = true;
        _dispatcher.SaveFailure = new DbUpdateConcurrencyException("The row changed.");

        await ProcessorWith(new FakeConverter(output: [9])).ProcessAsync(job, Ct);

        Assert.False(await ExistsAsync(job.SourceFileKey));
        Assert.False(await ExistsAsync(StorageKeys.ResultOf(job.Id)));
    }

    [Fact]
    public async Task AJobTakenOverByAnotherWorker_LeavesTheFilesToThatWorker()
    {
        var job = await ClaimedJobAsync();
        _dispatcher.SaveFailure = new DbUpdateConcurrencyException("The row changed.");

        await Assert.ThrowsAsync<DbUpdateConcurrencyException>(
            () => ProcessorWith(new FakeConverter(output: [9])).ProcessAsync(job, Ct));

        Assert.True(await ExistsAsync(job.SourceFileKey));
        Assert.True(await ExistsAsync(StorageKeys.ResultOf(job.Id)));
    }

    [Fact]
    public async Task AFinishedConversion_IsCountedAndTimed_ByFamilyAndOutcome()
    {
        var job = await ClaimedJobAsync();
        using var measurements = new RecordedMeasurements(_meters);

        await ProcessorWith(new FakeConverter(output: [1])).ProcessAsync(job, Ct);

        var completed = Assert.Single(measurements.Of("convertapi.jobs.completed"));
        Assert.Equal(1, completed.Value);
        Assert.Equal("Succeeded", completed.Tags["convertapi.job.status"]);
        Assert.Equal("Image", completed.Tags["convertapi.format.family"]);
        var duration = Assert.Single(measurements.Of("convertapi.conversion.duration"));
        Assert.True(duration.Value >= 0);
        Assert.Equal("Succeeded", duration.Tags["convertapi.job.status"]);
    }

    [Fact]
    public async Task TheConversionTrace_ContinuesTheTraceOfTheRequestThatCreatedTheJob()
    {
        const string RequestTrace = "00-0af7651916cd43dd8448eb211c80319c-b7ad6b7169203331-01";
        var job = await ClaimedJobAsync(RequestTrace);
        Activity? conversion = null;
        using var listener = new ActivityListener
        {
            ShouldListenTo = source => source.Name == ConversionTelemetry.SourceName,
            Sample = (ref ActivityCreationOptions<ActivityContext> _) => ActivitySamplingResult.AllDataAndRecorded,
            ActivityStopped = activity =>
            {
                if (Equals(activity.GetTagItem("convertapi.job.id"), job.Id.ToString()))
                {
                    conversion = activity;
                }
            },
        };
        ActivitySource.AddActivityListener(listener);

        await ProcessorWith(new FakeConverter(output: [1])).ProcessAsync(job, Ct);

        Assert.NotNull(conversion);
        Assert.Equal("0af7651916cd43dd8448eb211c80319c", conversion.TraceId.ToHexString());
        Assert.Equal("b7ad6b7169203331", conversion.ParentSpanId.ToHexString());
    }

    [Fact]
    public async Task AnAttemptThatThrows_EndsItsSpanInError()
    {
        var job = await ClaimedJobAsync();
        Activity? conversion = null;
        using var listener = new ActivityListener
        {
            ShouldListenTo = source => source.Name == ConversionTelemetry.SourceName,
            Sample = (ref ActivityCreationOptions<ActivityContext> _) => ActivitySamplingResult.AllDataAndRecorded,
            ActivityStopped = activity =>
            {
                if (Equals(activity.GetTagItem("convertapi.job.id"), job.Id.ToString()))
                {
                    conversion = activity;
                }
            },
        };
        ActivitySource.AddActivityListener(listener);

        await Assert.ThrowsAsync<IOException>(
            () => ProcessorWith(new FakeConverter(failure: new IOException("Disk full."))).ProcessAsync(job, Ct));

        Assert.NotNull(conversion);
        Assert.Equal(ActivityStatusCode.Error, conversion.Status);
    }

    private JobProcessor ProcessorWith(params IFileConverter[] converters) =>
        new(_storage, _dispatcher, converters, Options.Create(_options), new FixedClock(T0), _telemetry, NullLogger<JobProcessor>.Instance);

    private async Task<ConversionJob> ClaimedJobAsync(string? traceParent = null)
    {
        var key = StorageKeys.NewUpload();
        using var content = new MemoryStream(Source);
        await _storage.SaveAsync(key, content, maxBytes: 100, Ct);
        var job = ConversionJob.Create(Guid.NewGuid(), key, "png", "jpg", T0, traceParent: traceParent);
        job.Start(T0);
        return job;
    }

    private async Task<byte[]> ReadAsync(string key)
    {
        await using var stream = await _storage.OpenReadAsync(key, Ct);
        using var copy = new MemoryStream();
        await stream.CopyToAsync(copy, Ct);
        return copy.ToArray();
    }

    private async Task<bool> ExistsAsync(string key)
    {
        try
        {
            await using var stream = await _storage.OpenReadAsync(key, Ct);
            return true;
        }
        catch (FileNotFoundException)
        {
            return false;
        }
    }

    private sealed class FixedClock(DateTimeOffset now) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => now;
    }

    private sealed class RecordingDispatcher : IJobDispatcher
    {
        public List<ConversionJob> Saved { get; } = [];

        /// <summary>What <see cref="IsCanceledAsync"/> answers: the owner canceled the job.</summary>
        public bool Canceled { get; set; }

        /// <summary>Thrown by <see cref="SaveAsync"/>, as when the row changed since the claim.</summary>
        public Exception? SaveFailure { get; set; }

        public Task<ConversionJob?> ClaimNextAsync(DateTimeOffset now, CancellationToken cancellationToken) =>
            throw new NotSupportedException();

        public Task SaveAsync(ConversionJob job, CancellationToken cancellationToken)
        {
            if (SaveFailure is not null)
            {
                throw SaveFailure;
            }

            Saved.Add(job);
            return Task.CompletedTask;
        }

        /// <summary>How many <see cref="IsCanceledAsync"/> calls fail first, as when the database blips.</summary>
        public int FailingCancelChecks { get; set; }

        public Task<int> CountQueuedAsync(CancellationToken cancellationToken) => throw new NotSupportedException();

        public Task<bool> IsCanceledAsync(Guid jobId, CancellationToken cancellationToken) =>
            FailingCancelChecks-- > 0
                ? throw new TimeoutException("The database did not answer.")
                : Task.FromResult(Canceled);

        public Task ReleaseAsync(Guid jobId, DateTimeOffset now, CancellationToken cancellationToken) =>
            throw new NotSupportedException();
    }

    private sealed class FakeConverter(byte[]? output = null, Exception? failure = null, TimeSpan? delay = null) : IFileConverter
    {
        public byte[]? ReceivedInput { get; private set; }

        public (string Source, string Target)? ReceivedFormats { get; private set; }

        public string ReceivedInputPath { get; private set; } = "";

        public string ReceivedOutputPath { get; private set; } = "";

        public bool CanConvert(string sourceFormat, string targetFormat) => true;

        public async Task ConvertAsync(ConversionRequest request, CancellationToken cancellationToken)
        {
            ReceivedInput = await File.ReadAllBytesAsync(request.InputPath, cancellationToken);
            ReceivedFormats = (request.SourceFormat, request.TargetFormat);
            ReceivedInputPath = request.InputPath;
            ReceivedOutputPath = request.OutputPath;
            if (delay is { } wait)
            {
                await Task.Delay(wait, cancellationToken);
            }

            if (failure is not null)
            {
                throw failure;
            }

            await File.WriteAllBytesAsync(request.OutputPath, output!, cancellationToken);
        }
    }
}
