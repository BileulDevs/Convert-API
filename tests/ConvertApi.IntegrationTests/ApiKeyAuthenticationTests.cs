using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;

namespace ConvertApi.IntegrationTests;

public sealed class ApiKeyAuthenticationTests(ConvertApiFactory factory) : IClassFixture<ConvertApiFactory>
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static Uri UnknownJobUri => new($"/v1/jobs/{Guid.NewGuid()}", UriKind.Relative);

    [Fact]
    public async Task ARequestWithoutAKey_Gets401WithAChallenge()
    {
        using var client = factory.CreateClient();

        using var response = await client.GetAsync(UnknownJobUri, Ct);

        await AssertUnauthorizedAsync(response);
    }

    [Theory]
    [InlineData("")]
    [InlineData("not-a-key")]
    [InlineData("cvt_unknown1_ABCDEFGHIJKLMNOPQRSTUVWXYZabcdef")]
    public async Task ARequestWithAnInvalidKey_Gets401(string key)
    {
        using var client = factory.CreateClientWithKey(key);

        using var response = await client.GetAsync(UnknownJobUri, Ct);

        await AssertUnauthorizedAsync(response);
    }

    [Fact]
    public async Task AKnownKeyIdWithTheWrongSecret_Gets401()
    {
        var forged = $"cvt_{factory.ClientA.KeyId}_ABCDEFGHIJKLMNOPQRSTUVWXYZabcdef";
        using var client = factory.CreateClientWithKey(forged);

        using var response = await client.GetAsync(UnknownJobUri, Ct);

        await AssertUnauthorizedAsync(response);
    }

    [Fact]
    public async Task ARevokedKey_Gets401()
    {
        using var client = factory.CreateClientWithKey(factory.RevokedClient.Key);

        using var response = await client.GetAsync(UnknownJobUri, Ct);

        await AssertUnauthorizedAsync(response);
    }

    [Fact]
    public async Task AnUploadWithoutAKey_IsRejectedBeforeAnythingIsStored()
    {
        using var client = factory.CreateClient();
        var storedBefore = factory.StoredFiles().Length;
        using var form = new MultipartFormDataContent
        {
            { new StringContent("jpg"), "targetFormat" },
            { new ByteArrayContent([0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A]), "file", "photo.png" },
        };

        using var response = await client.PostAsync(new Uri("/v1/jobs", UriKind.Relative), form, Ct);

        await AssertUnauthorizedAsync(response);
        Assert.Equal(storedBefore, factory.StoredFiles().Length);
    }

    [Fact]
    public async Task AJob_IsInvisibleToAnotherKey()
    {
        using var owner = factory.CreateClientWithKey(factory.ClientA.Key);
        using var stranger = factory.CreateClientWithKey(factory.ClientB.Key);
        using var form = new MultipartFormDataContent
        {
            { new StringContent("jpg"), "targetFormat" },
            { new ByteArrayContent([0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A, 0x00]), "file", "photo.png" },
        };
        using var created = await owner.PostAsync(new Uri("/v1/jobs", UriKind.Relative), form, Ct);
        Assert.Equal(HttpStatusCode.Accepted, created.StatusCode);

        using var asOwner = await owner.GetAsync(created.Headers.Location, Ct);
        using var asStranger = await stranger.GetAsync(created.Headers.Location, Ct);

        Assert.Equal(HttpStatusCode.OK, asOwner.StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, asStranger.StatusCode);
    }

    [Theory]
    [InlineData("/health/live")]
    [InlineData("/health/ready")]
    [InlineData("/openapi/v1.json")]
    public async Task ProbesAndDocs_StayPublic(string path)
    {
        using var client = factory.CreateClient();

        using var response = await client.GetAsync(new Uri(path, UriKind.Relative), Ct);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
    }

    [Fact]
    public async Task TheOpenApiDocument_DeclaresTheApiKeyScheme()
    {
        using var client = factory.CreateClient();

        var document = await client.GetFromJsonAsync<JsonElement>(new Uri("/openapi/v1.json", UriKind.Relative), Ct);

        var scheme = document.GetProperty("components").GetProperty("securitySchemes").GetProperty("ApiKey");
        Assert.Equal("apiKey", scheme.GetProperty("type").GetString());
        Assert.Equal("header", scheme.GetProperty("in").GetString());
        Assert.Equal(ConvertApiFactory.ApiKeyHeader, scheme.GetProperty("name").GetString());
    }

    private static async Task AssertUnauthorizedAsync(HttpResponseMessage response)
    {
        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
        Assert.Contains(response.Headers.WwwAuthenticate, value => value.Scheme == "ApiKey");
        Assert.Equal("application/problem+json", response.Content.Headers.ContentType?.MediaType);
        var problem = await response.Content.ReadFromJsonAsync<JsonElement>(Ct);
        Assert.Equal(401, problem.GetProperty("status").GetInt32());
    }
}
