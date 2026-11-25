using ConvertApi.Api.Jobs;

namespace ConvertApi.IntegrationTests;

public sealed class JobTelemetryTests
{
    [Fact]
    public void TheMeterName_IsTheApisApplicationName_WhichServiceDefaultsRegisters()
    {
        Assert.Equal(JobTelemetry.MeterName, typeof(JobTelemetry).Assembly.GetName().Name);
    }
}
