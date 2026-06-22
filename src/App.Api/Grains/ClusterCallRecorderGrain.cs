using App.Api.GrainContracts;

namespace App.Api.Grains;

/// <summary>
/// Cluster-wide rolling buffer of the most recent grain calls. Every silo's
/// reporter forwards its batches here; the presenter view reads them back to draw
/// the communication lines. In-memory only — this is demo telemetry, not state
/// worth persisting.
/// </summary>
public sealed class ClusterCallRecorderGrain : Grain, IClusterCallRecorderGrain
{
    private readonly GrainCallRecord?[] _buffer = new GrainCallRecord[200];
    private int _nextIndex;
    private int _count;

    public Task Append(IReadOnlyList<GrainCallRecord> records)
    {
        foreach (var record in records)
        {
            _buffer[_nextIndex] = record;
            _nextIndex = (_nextIndex + 1) % _buffer.Length;
            _count = Math.Min(_count + 1, _buffer.Length);
        }

        return Task.CompletedTask;
    }

    public Task<IReadOnlyList<GrainCallRecord>> GetRecent()
    {
        var result = new List<GrainCallRecord>(_count);
        for (var i = 0; i < _count; i++)
        {
            var index = (_nextIndex - _count + i + _buffer.Length) % _buffer.Length;
            result.Add(_buffer[index]!);
        }

        return Task.FromResult<IReadOnlyList<GrainCallRecord>>(result);
    }
}
