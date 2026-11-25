using System.Net;
using System.Net.Http.Headers;
using ConvertApi.Core.Jobs;
using ConvertApi.Core.Storage;
using Microsoft.Extensions.DependencyInjection;

namespace ConvertApi.IntegrationTests;

/// <summary>Each test runs on its own database, so a key's active jobs are only the ones it adds.</summary>
public sealed class ActiveJobQuotaTests : IAsyncLifetime
{
    private const int MaxActiveJobs = 2;
    private static readonly byte[] Png = [0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A, 0, 0, 0, 0];

    private readonly ConvertApiFactory _factory = new();

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static Uri JobsUri => new("/v1/jobs", UriKind.Relative);

    public async ValueTask InitializeAsync()
    {
        await _factory.InitializeAsync();
        _factory.ConfigureSetting("RateLimit:MaxActiveJobs", MaxActiveJobs.ToString(System.Globalization.CultureInfo.InvariantCulture));
    }

    public async ValueTask DisposeAsync() => await _factory.DisposeAsync();

    [Fact]
    public async Task AKeyWithTooManyActiveJobs_Gets429_AndNothingIsStored()
    {
        await AddJobsAsync(_factory.ClientAOwnerId, j => { });
        await AddJobsAsync(_factory.ClientAOwnerId, j => j.Start(DateTimeOffset.UtcNow));
        var storedBefore = _factory.StoredFiles().Length;

        using var response = await PostAsync(_factory.ClientA.Key);

        Assert.Equal(HttpStatusCode.TooManyRequests, response.StatusCode);
        Assert.NotNull(response.Headers.RetryAfter?.Delta);
        Assert.Equal("application/problem+json", response.Content.Headers.ContentType?.MediaType);
        Assert.Equal(storedBefore, _factory.StoredFiles().Length);
    }

    [Fact]
    public async Task FinishedJobs_DoNotCount()
    {
        await AddJobsAsync(_factory.ClientAOwnerId, j => j.Cancel(DateTimeOffset.UtcNow), count: MaxActiveJobs);

        using var response = await PostAsync(_factory.ClientA.Key);

        Assert.Equal(HttpStatusCode.Accepted, response.StatusCode);
    }

    [Fact]
    public async Task EachKey_HasItsOwnQuota()
    {
        await AddJobsAsync(_factory.ClientAOwnerId, j => { }, count: MaxActiveJobs);

        using var response = await PostAsync(_factory.ClientB.Key);

        Assert.Equal(HttpStatusCode.Accepted, response.StatusCode);
    }

    private async Task<HttpResponseMessage> PostAsync(string key)
    {
        using var client = _factory.CreateClientWithKey(key);
        using var form = new MultipartFormDataContent { { new StringContent("jpg"), "targetFormat" } };
        var file = new ByteArrayContent(Png);
        file.Headers.ContentType = new MediaTypeHeaderValue("image/png");
        form.Add(file, "file", "photo.png");
        return await client.PostAsync(JobsUri, form, Ct);
    }

    private async Task AddJobsAsync(Guid ownerId, Action<ConversionJob> prepare, int count = 1)
    {
        await using var scope = _factory.Services.CreateAsyncScope();
        var jobs = scope.ServiceProvider.GetRequiredService<IJobRepository>();
        for (var i = 0; i < count; i++)
        {
            var job = ConversionJob.Create(ownerId, StorageKeys.NewUpload(), "png", "jpg", DateTimeOffset.UtcNow);
            prepare(job);
            await jobs.AddAsync(job, Ct);
        }
    }
}
