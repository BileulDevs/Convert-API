using System.Net;
using System.Net.ServerSentEvents;
using System.Text.Json;
using ConvertApi.Core.Jobs;
using ConvertApi.Core.Storage;
using ConvertApi.Data;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace ConvertApi.IntegrationTests;

/// <summary>GET /v1/jobs/{id}/events: the job's status as Server-Sent Events, until it is final.</summary>
public sealed class JobEventsTests(ConvertApiFactory factory) : IClassFixture<ConvertApiFactory>
{
    private static readonly TimeSpan EventTimeout = TimeSpan.FromSeconds(10);

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task AFinishedJob_SendsItsStatusOnce_ThenEndsTheStream()
    {
        var job = await AddJobAsync(j => j.Fail("Corrupt file.", DateTimeOffset.UtcNow));

        using var response = await OpenAsync(job.Id);
        var events = await ReadAllAsync(response);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal("text/event-stream", response.Content.Headers.ContentType?.MediaType);
        var only = Assert.Single(events);
        Assert.Equal("status", only.EventType);
        Assert.Equal("Failed", JsonDocument.Parse(only.Data).RootElement.GetProperty("status").GetString());
    }

    [Fact]
    public async Task ARunningJob_SendsEachChange_AndEndsOnceItSucceeds()
    {
        var job = await AddJobAsync();
        using var response = await OpenAsync(job.Id);
        await using var stream = await response.Content.ReadAsStreamAsync(Ct);
        await using var events = SseParser.Create(stream).EnumerateAsync(Ct).GetAsyncEnumerator(Ct);

        Assert.Equal("Queued", await NextStatusAsync(events));

        await UpdateAsync(job.Id, j => j.Start(DateTimeOffset.UtcNow));
        Assert.Equal("Processing", await NextStatusAsync(events));

        await UpdateAsync(job.Id, j => j.Succeed(TimeSpan.FromHours(1), DateTimeOffset.UtcNow));
        Assert.Equal("Succeeded", await NextStatusAsync(events));

        Assert.False(await events.MoveNextAsync().AsTask().WaitAsync(EventTimeout, Ct));
    }

    [Fact]
    public async Task AnotherKeysJob_Answers404()
    {
        var job = await AddJobAsync();
        using var stranger = factory.CreateClientWithKey(factory.ClientB.Key);

        using var response = await stranger.GetAsync(EventsUri(job.Id), Ct);

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    private static Uri EventsUri(Guid id) => new($"/v1/jobs/{id}/events", UriKind.Relative);

    private async Task<HttpResponseMessage> OpenAsync(Guid id)
    {
        using var client = factory.CreateClientWithKey();
        using var request = new HttpRequestMessage(HttpMethod.Get, EventsUri(id));
        return await client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, Ct);
    }

    private static async Task<List<SseItem<string>>> ReadAllAsync(HttpResponseMessage response)
    {
        await using var stream = await response.Content.ReadAsStreamAsync(Ct);
        var items = new List<SseItem<string>>();
        await foreach (var item in SseParser.Create(stream).EnumerateAsync(Ct).WithCancellation(Ct))
        {
            items.Add(item);
        }

        return items;
    }

    private static async Task<string?> NextStatusAsync(IAsyncEnumerator<SseItem<string>> events)
    {
        Assert.True(await events.MoveNextAsync().AsTask().WaitAsync(EventTimeout, Ct));
        return JsonDocument.Parse(events.Current.Data).RootElement.GetProperty("status").GetString();
    }

    private async Task<ConversionJob> AddJobAsync(Action<ConversionJob>? prepare = null)
    {
        var job = ConversionJob.Create(factory.ClientAOwnerId, StorageKeys.NewUpload(), "png", "jpg", DateTimeOffset.UtcNow);
        prepare?.Invoke(job);
        await using var scope = factory.Services.CreateAsyncScope();
        await scope.ServiceProvider.GetRequiredService<IJobRepository>().AddAsync(job, Ct);
        return job;
    }

    /// <summary>Changes the stored job, as a worker would.</summary>
    private async Task UpdateAsync(Guid id, Action<ConversionJob> change)
    {
        await using var scope = factory.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<ConvertApiDbContext>();
        change(await db.Jobs.SingleAsync(j => j.Id == id, Ct));
        await db.SaveChangesAsync(Ct);
    }
}
