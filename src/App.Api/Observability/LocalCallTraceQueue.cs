using System.Collections.Concurrent;
using App.Api.GrainContracts;

namespace App.Api.Observability;

/// <summary>
/// Per-silo, in-memory buffer of observed grain calls. The outgoing call filter
/// enqueues here on the hot path (cheap, lock-free) and the reporter grain
/// service drains it on a timer. Registered as a singleton so the filter and the
/// reporter share one instance per silo.
/// </summary>
public sealed class LocalCallTraceQueue
{
    private readonly ConcurrentQueue<GrainCallRecord> _queue = new();

    public void Enqueue(GrainCallRecord record) => _queue.Enqueue(record);

    /// <summary>Discards everything currently queued (used when tracing is turned off).</summary>
    public void Clear()
    {
        while (_queue.TryDequeue(out _))
        {
        }
    }

    /// <summary>Removes and returns up to <paramref name="max"/> queued records.</summary>
    public IReadOnlyList<GrainCallRecord> Drain(int max = 1_000)
    {
        var result = new List<GrainCallRecord>();
        while (result.Count < max && _queue.TryDequeue(out var record))
        {
            result.Add(record);
        }

        return result;
    }
}
