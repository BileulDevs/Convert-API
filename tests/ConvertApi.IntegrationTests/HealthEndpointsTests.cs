using System.Net;

namespace ConvertApi.IntegrationTests;

public sealed class HealthEndpointsTests(ConvertApiFactory factory)
    : IClassFixture<ConvertApiFactory>
{
    [Theory]
    [InlineData("/health/live")]
    [InlineData("/health/ready")]
    public async Task Probe_ReturnsHealthy(string path)
    {
        using var client = factory.CreateClient();

        using var response = await client.GetAsync(new Uri(path, UriKind.Relative), TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal("Healthy", await response.Content.ReadAsStringAsync(TestContext.Current.CancellationToken));
    }
}
