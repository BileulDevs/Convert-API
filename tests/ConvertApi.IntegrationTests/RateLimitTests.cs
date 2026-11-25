using System.Net;
using System.Net.Http.Json;
using System.Text.Json;

namespace ConvertApi.IntegrationTests;

/// <summary>Each test gets its own API, so the counters always start empty.</summary>
public sealed class RateLimitTests : IAsyncLifetime
{
    private const int PermitLimit = 2;

    private readonly ConvertApiFactory _factory = new();

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static Uri AnyJob => new($"/v1/jobs/{Guid.NewGuid()}", UriKind.Relative);

    public async ValueTask InitializeAsync()
    {
        await _factory.InitializeAsync();
        _factory.ConfigureSetting("RateLimit:PermitLimit", PermitLimit.ToString(System.Globalization.CultureInfo.InvariantCulture));
        _factory.ConfigureSetting("RateLimit:Window", "00:01:00");
    }

    public async ValueTask DisposeAsync() => await _factory.DisposeAsync();

    [Fact]
    public async Task AKeyOverItsLimit_Gets429_WithRetryAfter()
    {
        using var client = _factory.CreateClientWithKey();
        await UseUpTheLimitAsync(client);

        using var response = await client.GetAsync(AnyJob, Ct);

        Assert.Equal(HttpStatusCode.TooManyRequests, response.StatusCode);
        var retryAfter = response.Headers.RetryAfter?.Delta;
        Assert.NotNull(retryAfter);
        Assert.InRange(retryAfter.Value, TimeSpan.FromSeconds(1), TimeSpan.FromMinutes(1));
        Assert.Equal("application/problem+json", response.Content.Headers.ContentType?.MediaType);
        var problem = await response.Content.ReadFromJsonAsync<JsonElement>(Ct);
        Assert.Equal(429, problem.GetProperty("status").GetInt32());
    }

    [Fact]
    public async Task EachKey_HasItsOwnLimit()
    {
        using var first = _factory.CreateClientWithKey();
        await UseUpTheLimitAsync(first);
        using var second = _factory.CreateClientWithKey(_factory.ClientB.Key);

        using var response = await second.GetAsync(AnyJob, Ct);

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    [Fact]
    public async Task ARequestWithoutAValidKey_Gets401_Not429()
    {
        using var client = _factory.CreateClientWithKey();
        await UseUpTheLimitAsync(client);
        using var anonymous = _factory.CreateClient();

        using var response = await anonymous.GetAsync(AnyJob, Ct);

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    [Fact]
    public async Task HealthProbes_AreNotLimited()
    {
        using var client = _factory.CreateClientWithKey();
        for (var i = 0; i < PermitLimit + 2; i++)
        {
            using var response = await client.GetAsync(new Uri("/health/live", UriKind.Relative), Ct);
            Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        }
    }

    private static async Task UseUpTheLimitAsync(HttpClient client)
    {
        for (var i = 0; i < PermitLimit; i++)
        {
            using var response = await client.GetAsync(AnyJob, Ct);
            Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
        }
    }
}
