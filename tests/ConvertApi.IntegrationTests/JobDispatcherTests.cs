using ConvertApi.Core.Jobs;
using ConvertApi.Data;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace ConvertApi.IntegrationTests;

/// <summary>Each test runs on its own database: claims look at every queued job in it.</summary>
public sealed class JobDispatcherTests : IAsyncLifetime
{
    private static readonly DateTimeOffset T0 = new(2026, 11, 25, 10, 0, 0, TimeSpan.Zero);
    private static readonly TimeSpan Lease = TimeSpan.FromMinutes(15);

    private readonly ConvertApiFactory _factory = new();

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    public async ValueTask InitializeAsync()
    {
        await _factory.InitializeAsync();
        _factory.ConfigureSetting("Jobs:Dispatch:LeaseTimeout", Lease.ToString());
        _factory.ConfigureSetting("Jobs:Dispatch:MaxAttempts", "3");
        _ = _factory.Server;
    }

    public async ValueTask DisposeAsync() => await _factory.DisposeAsync();

    [Fact]
    public async Task ClaimNext_ReturnsNull_WhenNothingIsQueued()
    {
        Assert.Null(await ClaimAsync(T0));
    }

    [Fact]
    public async Task ClaimNext_StartsTheOldestQueuedJob()
    {
        var older = await AddJobAsync(T0);
        await AddJobAsync(T0.AddSeconds(1));

        var claimed = await ClaimAsync(T0.AddMinutes(1));

        Assert.NotNull(claimed);
        Assert.Equal(older.Id, claimed.Id);
        Assert.Equal(JobStatus.Processing, claimed.Status);
        Assert.Equal(1, claimed.Attempts);
        Assert.Equal(JobStatus.Processing, (await LoadAsync(older.Id)).Status);
    }

    [Fact]
    public async Task ConcurrentClaims_NeverHandOutTheSameJob()
    {
        for (var i = 0; i < 10; i++)
        {
            await AddJobAsync(T0.AddMilliseconds(i));
        }

        var claims = await Task.WhenAll(Enumerable.Range(0, 10).Select(_ => ClaimAsync(T0.AddMinutes(1))));

        var ids = claims.Select(job => job?.Id).ToList();
        Assert.DoesNotContain(null, ids);
        Assert.Equal(10, ids.Distinct().Count());
    }

    [Fact]
    public async Task ARunningJob_IsNotClaimedAgainWhileItsLeaseHolds()
    {
        await AddJobAsync(T0);
        await ClaimAsync(T0);

        Assert.Null(await ClaimAsync(T0 + Lease - TimeSpan.FromSeconds(1)));
    }

    [Fact]
    public async Task AJobWhoseWorkerStopped_IsClaimedAgainAfterTheLease()
    {
        var job = await AddJobAsync(T0);
        await ClaimAsync(T0);

        var reclaimed = await ClaimAsync(T0 + Lease + TimeSpan.FromSeconds(1));

        Assert.NotNull(reclaimed);
        Assert.Equal(job.Id, reclaimed.Id);
        Assert.Equal(2, reclaimed.Attempts);
    }

    [Fact]
    public async Task AJobThatKeepsCrashingItsWorker_FailsAfterTheMaximumAttempts()
    {
        var job = await AddJobAsync(T0);
        var now = T0;
        for (var attempt = 1; attempt <= 3; attempt++)
        {
            Assert.Equal(attempt, (await ClaimAsync(now))?.Attempts);
            now += Lease + TimeSpan.FromSeconds(1);
        }

        var last = await ClaimAsync(now);

        Assert.NotNull(last);
        Assert.Equal(JobStatus.Failed, last.Status);
        Assert.Equal(3, last.Attempts);
        Assert.Contains("3 attempts", last.FailureReason, StringComparison.Ordinal);
        Assert.Equal(JobStatus.Failed, (await LoadAsync(job.Id)).Status);
        Assert.Null(await ClaimAsync(now + Lease + Lease));
    }

    [Fact]
    public async Task AReleasedJob_CanBeClaimedAgainRightAway_WithoutLosingAnAttempt()
    {
        var job = await AddJobAsync(T0);
        await ClaimAsync(T0);
        await using (var scope = _factory.Services.CreateAsyncScope())
        {
            await scope.ServiceProvider.GetRequiredService<IJobDispatcher>().ReleaseAsync(job.Id, T0.AddSeconds(5), Ct);
        }

        var reclaimed = await ClaimAsync(T0.AddSeconds(6));

        Assert.NotNull(reclaimed);
        Assert.Equal(job.Id, reclaimed.Id);
        Assert.Equal(1, reclaimed.Attempts);
    }

    [Fact]
    public async Task ReleasingAFinishedJob_ChangesNothing()
    {
        var job = await AddJobAsync(T0);
        var claimed = await ClaimAsync(T0);
        await using (var scope = _factory.Services.CreateAsyncScope())
        {
            var dispatcher = scope.ServiceProvider.GetRequiredService<IJobDispatcher>();
            var tracked = await dispatcher.ClaimNextAsync(T0 + Lease + Lease, Ct);
            Assert.NotNull(tracked);
            tracked.Succeed(TimeSpan.FromHours(1), T0 + Lease + Lease);
            await dispatcher.SaveAsync(tracked, Ct);
            await dispatcher.ReleaseAsync(job.Id, T0 + Lease + Lease, Ct);
        }

        Assert.NotNull(claimed);
        Assert.Equal(JobStatus.Succeeded, (await LoadAsync(job.Id)).Status);
    }

    [Fact]
    public async Task FinishedJobs_AreNeverClaimed()
    {
        var canceled = ConversionJob.Create(Guid.NewGuid(), "uploads/canceled", "png", "jpg", T0);
        canceled.Cancel(T0);
        await AddAsync(canceled);

        Assert.Null(await ClaimAsync(T0.AddDays(1)));
    }

    [Fact]
    public async Task CountQueued_CountsOnlyJobsWaitingForAWorker()
    {
        await AddJobAsync(T0);
        await AddJobAsync(T0.AddSeconds(1));
        await AddJobAsync(T0.AddSeconds(2));
        await ClaimAsync(T0.AddMinutes(1));

        await using var scope = _factory.Services.CreateAsyncScope();
        Assert.Equal(2, await scope.ServiceProvider.GetRequiredService<IJobDispatcher>().CountQueuedAsync(Ct));
    }

    [Fact]
    public async Task Save_PersistsTheWorkersChanges()
    {
        await AddJobAsync(T0);
        await using var scope = _factory.Services.CreateAsyncScope();
        var dispatcher = scope.ServiceProvider.GetRequiredService<IJobDispatcher>();
        var job = await dispatcher.ClaimNextAsync(T0, Ct);
        Assert.NotNull(job);

        job.Succeed(TimeSpan.FromHours(24), T0.AddSeconds(5));
        await dispatcher.SaveAsync(job, Ct);

        Assert.Equal(JobStatus.Succeeded, (await LoadAsync(job.Id)).Status);
    }

    private async Task<ConversionJob> AddJobAsync(DateTimeOffset createdAt)
    {
        var job = ConversionJob.Create(Guid.NewGuid(), $"uploads/{Guid.NewGuid():N}", "png", "jpg", createdAt);
        await AddAsync(job);
        return job;
    }

    private async Task AddAsync(ConversionJob job)
    {
        await using var scope = _factory.Services.CreateAsyncScope();
        await scope.ServiceProvider.GetRequiredService<IJobRepository>().AddAsync(job, Ct);
    }

    private async Task<ConversionJob?> ClaimAsync(DateTimeOffset now)
    {
        await using var scope = _factory.Services.CreateAsyncScope();
        return await scope.ServiceProvider.GetRequiredService<IJobDispatcher>().ClaimNextAsync(now, Ct);
    }

    private async Task<ConversionJob> LoadAsync(Guid id)
    {
        await using var scope = _factory.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<ConvertApiDbContext>();
        return await db.Jobs.AsNoTracking().SingleAsync(j => j.Id == id, Ct);
    }
}
