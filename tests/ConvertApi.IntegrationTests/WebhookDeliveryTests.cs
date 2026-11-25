using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Threading.Channels;
using ConvertApi.Api.Webhooks;
using ConvertApi.Core.Jobs;
using ConvertApi.Core.Storage;
using ConvertApi.Data;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace ConvertApi.IntegrationTests;

/// <summary>
/// The API's webhook delivery, with the network replaced by a recording receiver.
/// Each test gets its own database, so the only due notifications are its own.
/// </summary>
public sealed class WebhookDeliveryTests : IAsyncLifetime
{
    private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(15);
    private static readonly Uri Callback = new("https://hooks.example.com/convert?client=42");

    private readonly ConvertApiFactory _factory = new();
    private readonly RecordingReceiver _receiver = new();

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    public async ValueTask InitializeAsync()
    {
        await _factory.InitializeAsync();
        _factory.ConfigureSetting("Webhooks:PollInterval", "00:00:00.100");
        _factory.TestServices = services =>
            services.AddHttpClient<WebhookSender>().ConfigurePrimaryHttpMessageHandler(() => _receiver);
        _ = _factory.Server;
    }

    public async ValueTask DisposeAsync()
    {
        await _factory.DisposeAsync();
        _receiver.Dispose();
    }

    [Fact]
    public async Task AFinishedJob_IsPostedOnce_SignedWithItsKeysSecret()
    {
        var job = await AddFinishedJobAsync(j => j.Fail("Corrupt file.", DateTimeOffset.UtcNow));

        var delivery = await _receiver.NextAsync().WaitAsync(Timeout, Ct);

        Assert.Equal(HttpMethod.Post, delivery.Method);
        Assert.Equal(Callback, delivery.Uri);
        Assert.Equal("application/json", delivery.ContentType);
        using var body = JsonDocument.Parse(delivery.Body);
        Assert.Equal(job.Id, body.RootElement.GetProperty("id").GetGuid());
        Assert.Equal("Failed", body.RootElement.GetProperty("status").GetString());
        AssertSignedBy(ConvertApiFactory.WebhookSecretOf(1), delivery);

        var stored = await WaitForAsync(job.Id, j => j.NotificationDueAt is null);
        Assert.Equal(1, stored.NotificationAttempts);
    }

    [Fact]
    public async Task AFailedDelivery_IsRetriedLater()
    {
        _receiver.Status = HttpStatusCode.InternalServerError;
        var job = await AddFinishedJobAsync(j => j.Cancel(DateTimeOffset.UtcNow));

        await _receiver.NextAsync().WaitAsync(Timeout, Ct);

        var stored = await WaitForAsync(job.Id, j => j.NotificationAttempts == 1 && j.NotificationDueAt > DateTimeOffset.UtcNow.AddSeconds(10));
        Assert.True(stored.NotificationDueAt < DateTimeOffset.UtcNow.AddMinutes(5));
    }

    [Fact]
    public async Task AJobWithoutACallback_IsNeverPosted()
    {
        var job = ConversionJob.Create(_factory.ClientAOwnerId, StorageKeys.NewUpload(), "png", "jpg", DateTimeOffset.UtcNow);
        job.Fail("Corrupt file.", DateTimeOffset.UtcNow);
        await AddAsync(job);
        var withCallback = await AddFinishedJobAsync(j => j.Fail("Corrupt file.", DateTimeOffset.UtcNow));

        var delivery = await _receiver.NextAsync().WaitAsync(Timeout, Ct);

        Assert.Equal(withCallback.Id, JsonDocument.Parse(delivery.Body).RootElement.GetProperty("id").GetGuid());
    }

    [Theory]
    [InlineData("127.0.0.1")]
    [InlineData("10.0.0.1")]
    [InlineData("::1")]
    [InlineData("localhost")]
    public async Task TheConnection_RefusesHostsThatAreNotPublic(string host)
    {
        await Assert.ThrowsAsync<HttpRequestException>(() => PublicOnlyConnector.ResolveAsync(host, Ct));
    }

    [Fact]
    public async Task TheConnection_AcceptsAPublicAddress()
    {
        Assert.Equal([IPAddress.Parse("8.8.8.8")], await PublicOnlyConnector.ResolveAsync("8.8.8.8", Ct));
    }

    private static void AssertSignedBy(string secret, Delivery delivery)
    {
        var parts = delivery.Signature.Split(',').Select(p => p.Split('=', 2)).ToDictionary(p => p[0], p => p[1]);
        var timestamp = long.Parse(parts["t"], System.Globalization.CultureInfo.InvariantCulture);
        Assert.InRange(DateTimeOffset.UtcNow.ToUnixTimeSeconds() - timestamp, 0, 60);
        byte[] signed = [.. Encoding.UTF8.GetBytes($"{timestamp}."), .. delivery.Body];
        var expected = HMACSHA256.HashData(Encoding.UTF8.GetBytes(secret), signed);
        Assert.Equal(Convert.ToHexStringLower(expected), parts["v1"]);
    }

    private async Task<ConversionJob> AddFinishedJobAsync(Action<ConversionJob> finish)
    {
        var job = ConversionJob.Create(_factory.ClientAOwnerId, StorageKeys.NewUpload(), "png", "jpg", DateTimeOffset.UtcNow, Callback);
        finish(job);
        await AddAsync(job);
        return job;
    }

    private async Task AddAsync(ConversionJob job)
    {
        await using var scope = _factory.Services.CreateAsyncScope();
        await scope.ServiceProvider.GetRequiredService<IJobRepository>().AddAsync(job, Ct);
    }

    private async Task<ConversionJob> WaitForAsync(Guid id, Func<ConversionJob, bool> condition)
    {
        var deadline = DateTimeOffset.UtcNow + Timeout;
        while (true)
        {
            await using (var scope = _factory.Services.CreateAsyncScope())
            {
                var db = scope.ServiceProvider.GetRequiredService<ConvertApiDbContext>();
                var job = await db.Jobs.AsNoTracking().SingleAsync(j => j.Id == id, Ct);
                if (condition(job))
                {
                    return job;
                }

                Assert.True(DateTimeOffset.UtcNow < deadline, "The job never reached the expected notification state.");
            }

            await Task.Delay(100, Ct);
        }
    }

    private sealed record Delivery(HttpMethod Method, Uri? Uri, string? ContentType, string Signature, byte[] Body);

    /// <summary>Stands in for the client's server: records each request and answers <see cref="Status"/>.</summary>
    private sealed class RecordingReceiver : HttpMessageHandler
    {
        private readonly Channel<Delivery> _deliveries = Channel.CreateUnbounded<Delivery>();

        public HttpStatusCode Status { get; set; } = HttpStatusCode.NoContent;

        public async Task<Delivery> NextAsync() => await _deliveries.Reader.ReadAsync(Ct);

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var body = request.Content is null ? [] : await request.Content.ReadAsByteArrayAsync(cancellationToken);
            var signature = request.Headers.TryGetValues(WebhookSender.SignatureHeader, out var values) ? values.Single() : "";
            await _deliveries.Writer.WriteAsync(
                new Delivery(request.Method, request.RequestUri, request.Content?.Headers.ContentType?.MediaType, signature, body),
                cancellationToken);
            return new HttpResponseMessage(Status);
        }
    }
}
