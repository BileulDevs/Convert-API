using System.Net;
using ConvertApi.Core.Jobs;
using ConvertApi.Core.Storage;
using ConvertApi.Data;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace ConvertApi.IntegrationTests;

/// <summary>DELETE /v1/jobs/{id}: cancels a job that has not finished, or deletes its result.</summary>
public sealed class JobDeleteTests(ConvertApiFactory factory) : IClassFixture<ConvertApiFactory>
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task AQueuedJob_IsCanceledAndItsUploadDeleted()
    {
        var job = await AddJobAsync();

        using var response = await DeleteAsync(job);

        Assert.Equal(HttpStatusCode.NoContent, response.StatusCode);
        Assert.Equal(JobStatus.Canceled, await StatusOfAsync(job));
        Assert.False(await ExistsAsync(job.SourceFileKey));
    }

    [Fact]
    public async Task ARunningJob_IsCanceled_SoItsWorkerStops()
    {
        var job = await AddJobAsync(j => j.Start(DateTimeOffset.UtcNow));

        using var response = await DeleteAsync(job);

        Assert.Equal(HttpStatusCode.NoContent, response.StatusCode);
        Assert.Equal(JobStatus.Canceled, await StatusOfAsync(job));
        await using var scope = factory.Services.CreateAsyncScope();
        Assert.True(await scope.ServiceProvider.GetRequiredService<IJobDispatcher>().IsCanceledAsync(job.Id, Ct));
    }

    [Fact]
    public async Task ASucceededJob_HasItsResultDeletedAtOnce()
    {
        var job = await AddJobAsync(j =>
        {
            j.Start(DateTimeOffset.UtcNow);
            j.Succeed(TimeSpan.FromHours(24), DateTimeOffset.UtcNow);
        });
        using (var result = new MemoryStream([1, 2, 3]))
        {
            await Storage.SaveAsync(StorageKeys.ResultOf(job.Id), result, 100, Ct);
        }

        using var response = await DeleteAsync(job);

        Assert.Equal(HttpStatusCode.NoContent, response.StatusCode);
        Assert.Equal(JobStatus.Expired, await StatusOfAsync(job));
        Assert.False(await ExistsAsync(StorageKeys.ResultOf(job.Id)));
        using var client = factory.CreateClientWithKey();
        using var download = await client.GetAsync(new Uri($"/v1/jobs/{job.Id}/result", UriKind.Relative), Ct);
        Assert.Equal(HttpStatusCode.Gone, download.StatusCode);
    }

    [Fact]
    public async Task AFinishedJob_IsLeftAsItIs_SoDeletingTwiceIsHarmless()
    {
        var job = await AddJobAsync(j => j.Fail("Corrupt file.", DateTimeOffset.UtcNow));

        using var response = await DeleteAsync(job);

        Assert.Equal(HttpStatusCode.NoContent, response.StatusCode);
        Assert.Equal(JobStatus.Failed, await StatusOfAsync(job));
    }

    [Fact]
    public async Task AnotherKeysJob_Answers404_AndIsLeftAlone()
    {
        var job = await AddJobAsync();
        using var stranger = factory.CreateClientWithKey(factory.ClientB.Key);

        using var response = await stranger.DeleteAsync(JobUri(job), Ct);

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
        Assert.Equal(JobStatus.Queued, await StatusOfAsync(job));
        Assert.True(await ExistsAsync(job.SourceFileKey));
    }

    [Fact]
    public async Task WithoutAKey_Answers401()
    {
        var job = await AddJobAsync();
        using var anonymous = factory.CreateClient();

        using var response = await anonymous.DeleteAsync(JobUri(job), Ct);

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    private IFileStorage Storage => factory.Services.GetRequiredService<IFileStorage>();

    private static Uri JobUri(ConversionJob job) => new($"/v1/jobs/{job.Id}", UriKind.Relative);

    private async Task<HttpResponseMessage> DeleteAsync(ConversionJob job)
    {
        using var client = factory.CreateClientWithKey();
        return await client.DeleteAsync(JobUri(job), Ct);
    }

    /// <summary>A job of client A with a stored upload, brought to a state by <paramref name="prepare"/>.</summary>
    private async Task<ConversionJob> AddJobAsync(Action<ConversionJob>? prepare = null)
    {
        var job = ConversionJob.Create(
            factory.ClientAOwnerId, StorageKeys.NewUpload(), "png", "jpg", DateTimeOffset.UtcNow.AddMinutes(-1));
        prepare?.Invoke(job);
        using (var upload = new MemoryStream([1, 2, 3]))
        {
            await Storage.SaveAsync(job.SourceFileKey, upload, 100, Ct);
        }

        await using var scope = factory.Services.CreateAsyncScope();
        await scope.ServiceProvider.GetRequiredService<IJobRepository>().AddAsync(job, Ct);
        return job;
    }

    private async Task<JobStatus> StatusOfAsync(ConversionJob job)
    {
        await using var scope = factory.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<ConvertApiDbContext>();
        return (await db.Jobs.SingleAsync(j => j.Id == job.Id, Ct)).Status;
    }

    private async Task<bool> ExistsAsync(string key)
    {
        try
        {
            await using var stream = await Storage.OpenReadAsync(key, Ct);
            return true;
        }
        catch (FileNotFoundException)
        {
            return false;
        }
    }
}
