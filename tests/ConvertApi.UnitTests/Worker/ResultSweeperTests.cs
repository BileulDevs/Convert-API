using ConvertApi.Core.Jobs;
using ConvertApi.Core.Storage;
using ConvertApi.Worker;
using Microsoft.Extensions.Logging.Abstractions;

namespace ConvertApi.UnitTests.Worker;

public sealed class ResultSweeperTests : IDisposable
{
    private static readonly DateTimeOffset T0 = new(2026, 11, 25, 10, 0, 0, TimeSpan.Zero);
    private static readonly TimeSpan Retention = TimeSpan.FromHours(24);

    private readonly string _root = Path.Combine(Path.GetTempPath(), "convertapi-tests", Guid.NewGuid().ToString("N"));
    private readonly LocalFileStorage _storage;
    private readonly InMemoryJobs _jobs = new();

    public ResultSweeperTests()
    {
        _storage = new LocalFileStorage(_root);
    }

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    public void Dispose()
    {
        if (Directory.Exists(_root))
        {
            Directory.Delete(_root, recursive: true);
        }
    }

    [Fact]
    public async Task AnExpiredResult_IsDeletedAndItsJobExpires()
    {
        var job = await SucceededJobWithResultAsync();

        var expired = await SweeperAt(T0 + Retention).SweepAsync(Ct);

        Assert.Equal(1, expired);
        Assert.Equal(JobStatus.Expired, job.Status);
        Assert.False(await ExistsAsync(StorageKeys.ResultOf(job.Id)));
        Assert.Same(job, Assert.Single(_jobs.Saved));
    }

    [Fact]
    public async Task AResultStillWithinItsRetention_IsKept()
    {
        var job = await SucceededJobWithResultAsync();

        var expired = await SweeperAt(T0 + Retention - TimeSpan.FromSeconds(1)).SweepAsync(Ct);

        Assert.Equal(0, expired);
        Assert.Equal(JobStatus.Succeeded, job.Status);
        Assert.True(await ExistsAsync(StorageKeys.ResultOf(job.Id)));
        Assert.Empty(_jobs.Saved);
    }

    [Fact]
    public async Task AResultAlreadyGone_StillExpiresItsJob()
    {
        var job = await SucceededJobWithResultAsync();
        await _storage.DeleteAsync(StorageKeys.ResultOf(job.Id), Ct);

        await SweeperAt(T0 + Retention).SweepAsync(Ct);

        Assert.Equal(JobStatus.Expired, job.Status);
    }

    [Fact]
    public async Task AResultThatCannotBeDeleted_KeepsItsJobForTheNextSweep_WithoutBlockingTheOthers()
    {
        var stuck = await SucceededJobWithResultAsync();
        var other = await SucceededJobWithResultAsync();
        var storage = new FailingDeleteStorage(_storage, StorageKeys.ResultOf(stuck.Id));

        var expired = await SweeperAt(T0 + Retention, storage).SweepAsync(Ct);

        Assert.Equal(1, expired);
        Assert.Equal(JobStatus.Succeeded, stuck.Status);
        Assert.Equal(JobStatus.Expired, other.Status);
    }

    [Fact]
    public async Task AResultWithoutDeletePermission_DoesNotStopTheSweep()
    {
        var stuck = await SucceededJobWithResultAsync();
        var other = await SucceededJobWithResultAsync();
        var storage = new FailingDeleteStorage(_storage, StorageKeys.ResultOf(stuck.Id), new UnauthorizedAccessException());

        await SweeperAt(T0 + Retention, storage).SweepAsync(Ct);

        Assert.Equal(JobStatus.Succeeded, stuck.Status);
        Assert.Equal(JobStatus.Expired, other.Status);
    }

    [Fact]
    public async Task OneSweep_HandlesAtMostOneBatch()
    {
        for (var i = 0; i < ResultSweeper.BatchSize + 1; i++)
        {
            await SucceededJobWithResultAsync();
        }

        var expired = await SweeperAt(T0 + Retention).SweepAsync(Ct);

        Assert.Equal(ResultSweeper.BatchSize, expired);
    }

    private ResultSweeper SweeperAt(DateTimeOffset now, IFileStorage? storage = null) =>
        new(_jobs, storage ?? _storage, new FixedClock(now), NullLogger<ResultSweeper>.Instance);

    private async Task<ConversionJob> SucceededJobWithResultAsync()
    {
        var job = ConversionJob.Create(Guid.NewGuid(), StorageKeys.NewUpload(), "png", "jpg", T0);
        job.Start(T0);
        job.Succeed(Retention, T0);
        using var result = new MemoryStream([1, 2, 3]);
        await _storage.SaveAsync(StorageKeys.ResultOf(job.Id), result, maxBytes: 100, Ct);
        _jobs.All.Add(job);
        return job;
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

    private sealed class InMemoryJobs : IJobRepository
    {
        public List<ConversionJob> All { get; } = [];

        public List<ConversionJob> Saved { get; } = [];

        public Task AddAsync(ConversionJob job, CancellationToken cancellationToken) => throw new NotSupportedException();

        public Task<ConversionJob?> FindAsync(Guid id, Guid ownerId, CancellationToken cancellationToken) =>
            throw new NotSupportedException();

        public Task<IReadOnlyList<ConversionJob>> ListExpiredAsync(DateTimeOffset now, int limit, CancellationToken cancellationToken) =>
            Task.FromResult<IReadOnlyList<ConversionJob>>(
                [.. All.Where(j => j.Status == JobStatus.Succeeded && j.ExpiresAt <= now).OrderBy(j => j.ExpiresAt).Take(limit)]);

        public Task SaveAsync(ConversionJob job, CancellationToken cancellationToken)
        {
            Saved.Add(job);
            return Task.CompletedTask;
        }
    }

    private sealed class FailingDeleteStorage(IFileStorage inner, string failingKey, Exception? failure = null) : IFileStorage
    {
        public Task<long> SaveAsync(string key, Stream content, long maxBytes, CancellationToken cancellationToken) =>
            inner.SaveAsync(key, content, maxBytes, cancellationToken);

        public Task<Stream> OpenReadAsync(string key, CancellationToken cancellationToken) =>
            inner.OpenReadAsync(key, cancellationToken);

        public Task DeleteAsync(string key, CancellationToken cancellationToken) =>
            key == failingKey ? throw failure ?? new IOException("The file is locked.") : inner.DeleteAsync(key, cancellationToken);
    }

    private sealed class FixedClock(DateTimeOffset now) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => now;
    }
}
