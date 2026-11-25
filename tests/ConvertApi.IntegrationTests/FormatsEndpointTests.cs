using System.Net;
using System.Net.Http.Json;
using System.Text.Json;

namespace ConvertApi.IntegrationTests;

/// <summary>GET /v1/formats: the conversions the service accepts, for clients to offer.</summary>
public sealed class FormatsEndpointTests(ConvertApiFactory factory) : IClassFixture<ConvertApiFactory>
{
    private static readonly Uri FormatsUri = new("/v1/formats", UriKind.Relative);

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task TheCatalog_IsPublic_AndCacheable()
    {
        using var anonymous = factory.CreateClient();

        using var response = await anonymous.GetAsync(FormatsUri, Ct);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.True(response.Headers.CacheControl?.Public);
        Assert.NotNull(response.Headers.CacheControl?.MaxAge);
    }

    [Fact]
    public async Task EachSource_ComesWithItsFamilyAndTargets()
    {
        using var client = factory.CreateClient();

        var formats = await ReadFormatsAsync(client);

        var png = formats.Single(f => f.GetProperty("source").GetString() == "png");
        Assert.Equal("Image", png.GetProperty("family").GetString());
        Assert.Equal(["bmp", "gif", "jpg", "png", "webp"], png.GetProperty("targets").EnumerateArray().Select(t => t.GetString()));
        var mp4 = formats.Single(f => f.GetProperty("source").GetString() == "mp4");
        Assert.Contains("mp3", mp4.GetProperty("targets").EnumerateArray().Select(t => t.GetString()));
    }

    [Fact]
    public async Task FormatsTheServiceCannotConvertYet_AreNotOffered()
    {
        using var client = factory.CreateClient();

        var sources = (await ReadFormatsAsync(client)).Select(f => f.GetProperty("source").GetString()).ToList();

        Assert.DoesNotContain("docx", sources);
        Assert.DoesNotContain("html", sources);
    }

    private static async Task<List<JsonElement>> ReadFormatsAsync(HttpClient client)
    {
        var body = await client.GetFromJsonAsync<JsonElement>(FormatsUri, Ct);
        return [.. body.GetProperty("formats").EnumerateArray()];
    }
}
