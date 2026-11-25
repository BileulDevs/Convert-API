using ConvertApi.Core.Jobs;
using ConvertApi.Data;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace ConvertApi.IntegrationTests;

public sealed class JobPersistenceTests(ConvertApiFactory factory) : IClassFixture<ConvertApiFactory>
{
    private static readonly DateTimeOffset T0 = new(2026, 11, 25, 10, 0, 0, TimeSpan.Zero);

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task AJob_RoundTripsWithItsStatusAndHistory()
    {
        var job = NewJob();
        job.Start(T0.AddSeconds(1));
        job.Start(T0.AddSeconds(2));
        job.Succeed(TimeSpan.FromHours(24), T0.AddSeconds(3));
        await SaveAsync(job);

        var loaded = await LoadAsync(job.Id);

        Assert.NotNull(loaded);
        Assert.Equal(job.OwnerId, loaded.OwnerId);
        Assert.Equal(job.SourceFileKey, loaded.SourceFileKey);
        Assert.Equal("png", loaded.SourceFormat);
        Assert.Equal("jpg", loaded.TargetFormat);
        Assert.Equal(JobStatus.Succeeded, loaded.Status);
        Assert.Equal(2, loaded.Attempts);
        Assert.Equal(T0, loaded.CreatedAt);
        Assert.Equal(T0.AddSeconds(2), loaded.StartedAt);
        Assert.Equal(T0.AddSeconds(3), loaded.CompletedAt);
        Assert.Equal(T0.AddSeconds(3).AddHours(24), loaded.ExpiresAt);
        Assert.Equal(job.Transitions, loaded.Transitions);
    }

    [Fact]
    public async Task ALoadedJob_KeepsEnforcingItsStateMachine()
    {
        var job = NewJob();
        job.Cancel(T0);
        await SaveAsync(job);

        var loaded = await LoadAsync(job.Id);

        Assert.NotNull(loaded);
        Assert.Throws<InvalidJobTransitionException>(() => loaded.Start(T0.AddMinutes(1)));
    }

    [Fact]
    public async Task TwoWorkersSavingTheSameJob_OnlyTheFirstWins()
    {
        var job = NewJob();
        await SaveAsync(job);

        await using var firstScope = factory.Services.CreateAsyncScope();
        await using var secondScope = factory.Services.CreateAsyncScope();
        var firstDb = firstScope.ServiceProvider.GetRequiredService<ConvertApiDbContext>();
        var secondDb = secondScope.ServiceProvider.GetRequiredService<ConvertApiDbContext>();
        var first = await firstDb.Jobs.SingleAsync(j => j.Id == job.Id, Ct);
        var second = await secondDb.Jobs.SingleAsync(j => j.Id == job.Id, Ct);

        first.Start(T0.AddSeconds(1));
        await firstDb.SaveChangesAsync(Ct);
        second.Start(T0.AddSeconds(1));

        await Assert.ThrowsAsync<DbUpdateConcurrencyException>(() => secondDb.SaveChangesAsync(Ct));
    }

    [Fact]
    public async Task AddingTheSameJobTwice_IsHarmless_SoARetriedInsertDoesNotFail()
    {
        var job = NewJob();
        await SaveAsync(job);

        await SaveAsync(job);

        Assert.NotNull(await LoadAsync(job.Id));
    }

    [Fact]
    public async Task TheSchema_HasTheIndexesThatListingAndExpiryNeed()
    {
        await using var scope = factory.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<ConvertApiDbContext>();

        var indexes = await db.Database
            .SqlQuery<string>($"SELECT indexdef AS \"Value\" FROM pg_indexes WHERE tablename = 'Jobs'")
            .ToListAsync(Ct);

        Assert.Contains(indexes, index => index.Contains("(\"OwnerId\", \"Id\")", StringComparison.Ordinal));
        Assert.Contains(indexes, index =>
            index.Contains("(\"ExpiresAt\")", StringComparison.Ordinal)
            && index.Contains("'Succeeded'", StringComparison.Ordinal));
    }

    [Fact]
    public async Task TheRepository_OnlyFindsAJobForItsOwner()
    {
        var job = NewJob();
        await SaveAsync(job);
        await using var scope = factory.Services.CreateAsyncScope();
        var jobs = scope.ServiceProvider.GetRequiredService<IJobRepository>();

        Assert.NotNull(await jobs.FindAsync(job.Id, job.OwnerId, Ct));
        Assert.Null(await jobs.FindAsync(job.Id, Guid.NewGuid(), Ct));
    }

    private static ConversionJob NewJob() =>
        ConversionJob.Create(Guid.NewGuid(), $"uploads/{Guid.NewGuid():N}", "png", "jpg", T0);

    private async Task SaveAsync(ConversionJob job)
    {
        await using var scope = factory.Services.CreateAsyncScope();
        await scope.ServiceProvider.GetRequiredService<IJobRepository>().AddAsync(job, Ct);
    }

    private async Task<ConversionJob?> LoadAsync(Guid id)
    {
        await using var scope = factory.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<ConvertApiDbContext>();
        return await db.Jobs.AsNoTracking().SingleOrDefaultAsync(j => j.Id == id, Ct);
    }
}
