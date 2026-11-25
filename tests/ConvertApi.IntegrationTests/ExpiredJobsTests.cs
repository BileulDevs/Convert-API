using ConvertApi.Core.Jobs;
using ConvertApi.Data;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace ConvertApi.IntegrationTests;

/// <summary>Each test runs on its own database: the sweep looks at every job in it.</summary>
public sealed class ExpiredJobsTests : IAsyncLifetime
{
    private static readonly DateTimeOffset T0 = new(2026, 11, 25, 10, 0, 0, TimeSpan.Zero);
    private static readonly TimeSpan Retention = TimeSpan.FromHours(24);

    private readonly ConvertApiFactory _factory = new();

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    public async ValueTask InitializeAsync()
    {
        await _factory.InitializeAsync();
        _ = _factory.Server;
    }

    public async ValueTask DisposeAsync() => await _factory.DisposeAsync();

    [Fact]
    public async Task ListExpired_ReturnsSucceededJobsPastTheirExpiry_OldestFirst()
    {
        var later = await AddSucceededAsync(T0.AddMinutes(1));
        var earlier = await AddSucceededAsync(T0);
        await AddSucceededAsync(T0.AddHours(1));

        var expired = await ListExpiredAsync(T0.AddMinutes(1) + Retention, limit: 10);

        Assert.Equal([earlier.Id, later.Id], expired.Select(j => j.Id));
    }

    [Fact]
    public async Task ListExpired_IgnoresJobsThatHoldNoResult()
    {
        var failed = NewJob(T0);
        failed.Fail("Corrupt file.", T0);
        await AddAsync(failed);
        var expired = await AddSucceededAsync(T0);
        await ExpireAsync(expired.Id);

        Assert.Empty(await ListExpiredAsync(T0 + Retention * 2, limit: 10));
    }

    [Fact]
    public async Task ListExpired_StopsAtTheLimit()
    {
        for (var i = 0; i < 3; i++)
        {
            await AddSucceededAsync(T0.AddSeconds(i));
        }

        Assert.Equal(2, (await ListExpiredAsync(T0.AddDays(2), limit: 2)).Count);
    }

    [Fact]
    public async Task Save_PersistsTheExpiry()
    {
        var job = await AddSucceededAsync(T0);

        await ExpireAsync(job.Id);

        await using var scope = _factory.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<ConvertApiDbContext>();
        var loaded = await db.Jobs.SingleAsync(j => j.Id == job.Id, Ct);
        Assert.Equal(JobStatus.Expired, loaded.Status);
    }

    private static ConversionJob NewJob(DateTimeOffset createdAt) =>
        ConversionJob.Create(Guid.NewGuid(), $"uploads/{Guid.NewGuid():N}", "png", "jpg", createdAt);

    private async Task<ConversionJob> AddSucceededAsync(DateTimeOffset completedAt)
    {
        var job = NewJob(completedAt);
        job.Start(completedAt);
        job.Succeed(Retention, completedAt);
        await AddAsync(job);
        return job;
    }

    private async Task AddAsync(ConversionJob job)
    {
        await using var scope = _factory.Services.CreateAsyncScope();
        await scope.ServiceProvider.GetRequiredService<IJobRepository>().AddAsync(job, Ct);
    }

    /// <summary>Expires the job the way the sweeper does: load it through the repository, then save.</summary>
    private async Task ExpireAsync(Guid id)
    {
        await using var scope = _factory.Services.CreateAsyncScope();
        var jobs = scope.ServiceProvider.GetRequiredService<IJobRepository>();
        var job = (await jobs.ListExpiredAsync(T0.AddYears(1), limit: 100, Ct)).Single(j => j.Id == id);
        job.Expire(T0 + Retention);
        await jobs.SaveAsync(job, Ct);
    }

    private async Task<IReadOnlyList<ConversionJob>> ListExpiredAsync(DateTimeOffset now, int limit)
    {
        await using var scope = _factory.Services.CreateAsyncScope();
        return await scope.ServiceProvider.GetRequiredService<IJobRepository>().ListExpiredAsync(now, limit, Ct);
    }
}
