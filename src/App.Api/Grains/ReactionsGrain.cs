using App.Api.GrainContracts;

namespace App.Api.Grains;

/// <summary>
/// In-memory feed of recent attendee reactions. Each <see cref="Push"/> appends a
/// sequenced event; <see cref="GetSince"/> returns events newer than the caller's
/// cursor and prunes anything past the retention window so the buffer stays
/// bounded. State is intentionally volatile — reactions are ephemeral floating
/// emoji and a single activation serves the whole cluster.
/// </summary>
public sealed class ReactionsGrain : Grain, IReactionsGrain
{
    // Reactions older than this are dropped. The presenter polls every couple of
    // seconds, so this is plenty of slack while keeping the buffer tiny.
    private static readonly TimeSpan Retention = TimeSpan.FromSeconds(30);

    // Hard cap regardless of time, so a flood of taps can't grow memory unbounded.
    private const int MaxBuffered = 500;

    private readonly List<(long Seq, string Kind, DateTimeOffset At)> _events = new();
    private long _nextSeq;

    public Task Push(string kind)
    {
        if (!IReactionsGrain.AllowedKinds.Contains(kind))
        {
            return Task.CompletedTask;
        }

        _events.Add((++_nextSeq, kind, DateTimeOffset.UtcNow));
        Prune();
        return Task.CompletedTask;
    }

    public Task<ReactionFeed> GetSince(long? afterSeq)
    {
        Prune();

        // No cursor yet: hand back the current position with no backlog so a
        // presenter that just connected doesn't replay reactions from before.
        if (afterSeq is null)
        {
            return Task.FromResult(new ReactionFeed(_nextSeq, []));
        }

        var events = _events
            .Where(e => e.Seq > afterSeq.Value)
            .Select(e => new ReactionEvent(e.Seq, e.Kind))
            .ToArray();

        return Task.FromResult(new ReactionFeed(_nextSeq, events));
    }

    private void Prune()
    {
        var cutoff = DateTimeOffset.UtcNow - Retention;
        _events.RemoveAll(e => e.At < cutoff);

        if (_events.Count > MaxBuffered)
        {
            _events.RemoveRange(0, _events.Count - MaxBuffered);
        }
    }
}
