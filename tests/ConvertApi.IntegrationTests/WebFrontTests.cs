using System.Net;
using System.Security.Cryptography;
using System.Text;

namespace ConvertApi.IntegrationTests;

/// <summary>The web front, built static files the API serves from its own origin.</summary>
public sealed class WebFrontTests : IAsyncLifetime
{
    private const string InlineScript = "window.__NUXT__={};";

    private readonly ConvertApiFactory _factory = new();
    private readonly string _root = Path.Combine(Path.GetTempPath(), "convertapi-web-tests", Guid.NewGuid().ToString("N"));

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    public async ValueTask InitializeAsync()
    {
        Directory.CreateDirectory(Path.Combine(_root, "_nuxt"));
        await File.WriteAllTextAsync(
            Path.Combine(_root, "index.html"),
            $"""<!DOCTYPE html><html><head><script type="module" src="/_nuxt/app.js"></script><script>{InlineScript}</script><script type="application/json" id="__NUXT_DATA__">[1]</script></head><body></body></html>""",
            Ct);
        await File.WriteAllTextAsync(Path.Combine(_root, "_nuxt", "app.js"), "console.log(1)", Ct);

        await _factory.InitializeAsync();
        _factory.ConfigureSetting("Web:Root", _root);
    }

    public async ValueTask DisposeAsync()
    {
        await _factory.DisposeAsync();
        Directory.Delete(_root, recursive: true);
    }

    [Fact]
    public async Task TheHomePage_IsServedWithoutAKey_UnderAStrictContentSecurityPolicy()
    {
        using var client = _factory.CreateClient();

        using var response = await client.GetAsync(new Uri("/", UriKind.Relative), Ct);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal("text/html", response.Content.Headers.ContentType?.MediaType);
        var policy = string.Join(';', response.Headers.GetValues("Content-Security-Policy"));
        Assert.Contains("default-src 'self'", policy, StringComparison.Ordinal);
        Assert.Contains("frame-ancestors 'none'", policy, StringComparison.Ordinal);
        Assert.DoesNotContain("unsafe-inline' 'self", policy, StringComparison.Ordinal);
        Assert.Equal("nosniff", response.Headers.GetValues("X-Content-Type-Options").Single());
        Assert.True(response.Headers.CacheControl?.NoCache);
    }

    [Fact]
    public async Task OnlyThePagesOwnInlineScripts_AreAllowed()
    {
        using var client = _factory.CreateClient();

        using var response = await client.GetAsync(new Uri("/", UriKind.Relative), Ct);

        var policy = string.Join(';', response.Headers.GetValues("Content-Security-Policy"));
        var hash = Convert.ToBase64String(SHA256.HashData(Encoding.UTF8.GetBytes(InlineScript)));
        Assert.Contains($"'sha256-{hash}'", policy, StringComparison.Ordinal);
        Assert.DoesNotContain("'unsafe-inline'", policy.Split(';').Single(d => d.Trim().StartsWith("script-src", StringComparison.Ordinal)), StringComparison.Ordinal);
    }

    [Fact]
    public async Task BuiltAssets_AreCachedForGood()
    {
        using var client = _factory.CreateClient();

        using var response = await client.GetAsync(new Uri("/_nuxt/app.js", UriKind.Relative), Ct);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Contains("immutable", response.Headers.CacheControl?.ToString(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task AnUnknownApiPath_StaysA404_NotThePage()
    {
        using var client = _factory.CreateClientWithKey();

        using var response = await client.GetAsync(new Uri("/v1/nothing-here", UriKind.Relative), Ct);

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }
}
