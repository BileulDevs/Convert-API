using System.Diagnostics.Metrics;
using ConvertApi.Worker;
using Microsoft.Extensions.DependencyInjection;

namespace ConvertApi.UnitTests.Worker;

public sealed class ConversionTelemetryTests : IDisposable
{
    private readonly ServiceProvider _services = new ServiceCollection().AddMetrics().BuildServiceProvider();

    public void Dispose() => _services.Dispose();

    [Fact]
    public void TheQueueGauge_ReportsNothingUntilTheFirstSample_ThenTheLatestOne()
    {
        var meters = _services.GetRequiredService<IMeterFactory>();
        var telemetry = new ConversionTelemetry(meters);
        using var measurements = new RecordedMeasurements(meters);

        Assert.Empty(measurements.Of("convertapi.jobs.queued"));

        telemetry.SetQueueLength(4);
        telemetry.SetQueueLength(7);

        Assert.Equal(7, Assert.Single(measurements.Of("convertapi.jobs.queued")).Value);
    }

    [Fact]
    public void TheSourceName_IsTheWorkersApplicationName_WhichServiceDefaultsRegisters()
    {
        Assert.Equal(ConversionTelemetry.SourceName, typeof(ConversionTelemetry).Assembly.GetName().Name);
    }
}
