using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using ConvertApi.Core.Jobs;
using ConvertApi.Core.Storage;
using Microsoft.Extensions.DependencyInjection;

namespace ConvertApi.IntegrationTests;

/// <summary>GET /v1/jobs/{id}/result, with jobs prepared in the database as the worker would leave them.</summary>
public sealed class JobResultTests(ConvertApiFactory factory) : IClassFixture<ConvertApiFactory>
{
    private static readonly byte[] JpegResult = [0xFF, 0xD8, 0xFF, 0xE0, 1, 2, 3, 4, 5, 6, 7, 8, 9, 10];

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task ASucceededJob_StreamsItsResultAsAnAttachment()
    {
        var job = await SucceededJobAsync();
        using var client = factory.CreateClientWithKey();

        using var response = await client.GetAsync(ResultUri(job), Ct);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal("image/jpeg", response.Content.Headers.ContentType?.MediaType);
        Assert.Equal("attachment", response.Content.Headers.ContentDisposition?.DispositionType);
        Assert.Equal($"{job.Id}.jpg", response.Content.Headers.ContentDisposition?.FileNameStar);
        Assert.Equal(JpegResult, await response.Content.ReadAsByteArrayAsync(Ct));
        Assert.True(response.Headers.CacheControl?.NoStore);
        Assert.True(response.Headers.CacheControl?.Private);
    }

    [Fact]
    public async Task ARangeRequest_GetsJustThatPart()
    {
        var job = await SucceededJobAsync();
        using var client = factory.CreateClientWithKey();
        using var request = new HttpRequestMessage(HttpMethod.Get, ResultUri(job));
        request.Headers.Range = new RangeHeaderValue(4, 7);

        using var response = await client.SendAsync(request, Ct);

        Assert.Equal(HttpStatusCode.PartialContent, response.StatusCode);
        Assert.Equal(JpegResult[4..8], await response.Content.ReadAsByteArrayAsync(Ct));
        Assert.Equal(JpegResult.Length, response.Content.Headers.ContentRange?.Length);
    }

    [Theory]
    [InlineData(JobStatus.Queued)]
    [InlineData(JobStatus.Processing)]
    public async Task AJobStillRunning_Answers409(JobStatus status)
    {
        var job = NewJob();
        if (status == JobStatus.Processing)
        {
            job.Start(DateTimeOffset.UtcNow);
        }

        await SaveAsync(job);
        using var client = factory.CreateClientWithKey();

        using var response = await client.GetAsync(ResultUri(job), Ct);

        var problem = await AssertProblemAsync(response, HttpStatusCode.Conflict);
        Assert.Contains("not finished", problem.GetProperty("detail").GetString(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task AFailedJob_Answers409WithItsReason()
    {
        var job = NewJob();
        job.Start(DateTimeOffset.UtcNow);
        job.Fail("The image is corrupt.", DateTimeOffset.UtcNow);
        await SaveAsync(job);
        using var client = factory.CreateClientWithKey();

        using var response = await client.GetAsync(ResultUri(job), Ct);

        var problem = await AssertProblemAsync(response, HttpStatusCode.Conflict);
        Assert.Contains("The image is corrupt.", problem.GetProperty("detail").GetString(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task AnExpiredJob_Answers410()
    {
        var job = await SucceededJobAsync();
        await using (var scope = factory.Services.CreateAsyncScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<ConvertApi.Data.ConvertApiDbContext>();
            var tracked = await db.Jobs.FindAsync([job.Id], Ct);
            tracked!.Expire(DateTimeOffset.UtcNow);
            await db.SaveChangesAsync(Ct);
        }

        using var client = factory.CreateClientWithKey();

        using var response = await client.GetAsync(ResultUri(job), Ct);

        await AssertProblemAsync(response, HttpStatusCode.Gone);
    }

    [Fact]
    public async Task AResultPastItsExpiryDate_Answers410_EvenBeforeTheSweepDeletesIt()
    {
        var job = await SucceededJobAsync(completedAt: DateTimeOffset.UtcNow.AddDays(-2));
        using var client = factory.CreateClientWithKey();

        using var response = await client.GetAsync(ResultUri(job), Ct);

        await AssertProblemAsync(response, HttpStatusCode.Gone);
    }

    [Fact]
    public async Task ASucceededJobWhoseFileIsGone_Answers410()
    {
        var job = await SucceededJobAsync();
        await factory.Services.GetRequiredService<IFileStorage>().DeleteAsync(StorageKeys.ResultOf(job.Id), Ct);
        using var client = factory.CreateClientWithKey();

        using var response = await client.GetAsync(ResultUri(job), Ct);

        await AssertProblemAsync(response, HttpStatusCode.Gone);
    }

    [Fact]
    public async Task AnotherKeysResult_Answers404()
    {
        var job = await SucceededJobAsync();
        using var stranger = factory.CreateClientWithKey(factory.ClientB.Key);

        using var response = await stranger.GetAsync(ResultUri(job), Ct);

        await AssertProblemAsync(response, HttpStatusCode.NotFound);
    }

    [Fact]
    public async Task WithoutAKey_Answers401()
    {
        var job = await SucceededJobAsync();
        using var anonymous = factory.CreateClient();

        using var response = await anonymous.GetAsync(ResultUri(job), Ct);

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    private static Uri ResultUri(ConversionJob job) => new($"/v1/jobs/{job.Id}/result", UriKind.Relative);

    private ConversionJob NewJob() =>
        ConversionJob.Create(factory.ClientAOwnerId, StorageKeys.NewUpload(), "png", "jpg", DateTimeOffset.UtcNow.AddMinutes(-1));

    private async Task<ConversionJob> SucceededJobAsync(DateTimeOffset? completedAt = null)
    {
        var at = completedAt ?? DateTimeOffset.UtcNow;
        var job = ConversionJob.Create(factory.ClientAOwnerId, StorageKeys.NewUpload(), "png", "jpg", at.AddMinutes(-1));
        job.Start(at.AddSeconds(-30));
        job.Succeed(TimeSpan.FromHours(24), at);
        using var content = new MemoryStream(JpegResult);
        await factory.Services.GetRequiredService<IFileStorage>().SaveAsync(StorageKeys.ResultOf(job.Id), content, 1024, Ct);
        await SaveAsync(job);
        return job;
    }

    private async Task SaveAsync(ConversionJob job)
    {
        await using var scope = factory.Services.CreateAsyncScope();
        await scope.ServiceProvider.GetRequiredService<IJobRepository>().AddAsync(job, Ct);
    }

    private static async Task<JsonElement> AssertProblemAsync(HttpResponseMessage response, HttpStatusCode expected)
    {
        Assert.Equal(expected, response.StatusCode);
        Assert.Equal("application/problem+json", response.Content.Headers.ContentType?.MediaType);
        return await response.Content.ReadFromJsonAsync<JsonElement>(Ct);
    }
}
