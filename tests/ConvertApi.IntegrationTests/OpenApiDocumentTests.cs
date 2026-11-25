using System.Net;
using Microsoft.AspNetCore.Mvc.Testing;

namespace ConvertApi.IntegrationTests;

public sealed class OpenApiDocumentTests(WebApplicationFactory<Program> factory)
    : IClassFixture<WebApplicationFactory<Program>>
{
    [Fact]
    public async Task OpenApiDocument_IsServedInDevelopment()
    {
        using var client = factory.CreateClient();

        using var response = await client.GetAsync(new Uri("/openapi/v1.json", UriKind.Relative), TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Contains("\"openapi\"", await response.Content.ReadAsStringAsync(TestContext.Current.CancellationToken), StringComparison.Ordinal);
    }
}
