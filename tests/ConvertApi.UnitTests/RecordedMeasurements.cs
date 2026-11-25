using System.Diagnostics.Metrics;

namespace ConvertApi.UnitTests;

/// <summary>
/// Records what the meters of one <see cref="IMeterFactory"/> measure, so a test sees only its own
/// measurements even while parallel tests record into same-named meters of other factories.
/// </summary>
internal sealed class RecordedMeasurements : IDisposable
{
    private readonly MeterListener _listener = new();
    private readonly List<(string Instrument, double Value, Dictionary<string, object?> Tags)> _measurements = [];

    public RecordedMeasurements(IMeterFactory meters)
    {
        _listener.InstrumentPublished = (instrument, listener) =>
        {
            if (ReferenceEquals(instrument.Meter.Scope, meters))
            {
                listener.EnableMeasurementEvents(instrument);
            }
        };
        _listener.SetMeasurementEventCallback<long>((instrument, value, tags, _) => Add(instrument, value, tags));
        _listener.SetMeasurementEventCallback<double>((instrument, value, tags, _) => Add(instrument, value, tags));
        _listener.SetMeasurementEventCallback<int>((instrument, value, tags, _) => Add(instrument, value, tags));
        _listener.Start();
    }

    public IReadOnlyList<(double Value, Dictionary<string, object?> Tags)> Of(string instrument)
    {
        _listener.RecordObservableInstruments();
        lock (_measurements)
        {
            return [.. _measurements.Where(m => m.Instrument == instrument).Select(m => (m.Value, m.Tags))];
        }
    }

    public void Dispose() => _listener.Dispose();

    private void Add(Instrument instrument, double value, ReadOnlySpan<KeyValuePair<string, object?>> tags)
    {
        var copy = new Dictionary<string, object?>();
        foreach (var tag in tags)
        {
            copy[tag.Key] = tag.Value;
        }

        lock (_measurements)
        {
            _measurements.Add((instrument.Name, value, copy));
        }
    }
}
