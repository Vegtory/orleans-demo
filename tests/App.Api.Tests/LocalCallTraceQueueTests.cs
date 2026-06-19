using App.Api.GrainContracts;
using App.Api.Observability;

namespace App.Api.Tests;

/// <summary>
/// Pure unit tests for the per-silo call-trace buffer (no cluster needed).
/// </summary>
public sealed class LocalCallTraceQueueTests
{
    private static GrainCallRecord Rec(string target) =>
        new(DateTimeOffset.UtcNow, "siloA", "src", target, "IFace", "M", 1, true);

    [Fact]
    public void Drain_returns_records_fifo_and_empties_the_queue()
    {
        var queue = new LocalCallTraceQueue();
        queue.Enqueue(Rec("t1"));
        queue.Enqueue(Rec("t2"));

        var drained = queue.Drain();

        Assert.Equal(new[] { "t1", "t2" }, drained.Select(r => r.TargetGrainId));
        Assert.Empty(queue.Drain()); // already drained
    }

    [Fact]
    public void Drain_respects_max_and_leaves_the_remainder_queued()
    {
        var queue = new LocalCallTraceQueue();
        for (var i = 0; i < 5; i++)
        {
            queue.Enqueue(Rec($"t{i}"));
        }

        var batch = queue.Drain(max: 2);

        Assert.Equal(2, batch.Count);
        Assert.Equal(3, queue.Drain().Count);
    }
}
