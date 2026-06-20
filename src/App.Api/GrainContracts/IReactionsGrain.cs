namespace App.Api.GrainContracts;

/// <summary>
/// A single reaction press, stamped with a monotonically increasing sequence
/// number so the presenter can poll for "everything newer than what I've already
/// shown" and animate each press exactly once.
/// </summary>
[GenerateSerializer]
public sealed record ReactionEvent(
    [property: Id(0)] long Seq,
    [property: Id(1)] string Kind);

/// <summary>
/// The presenter's view of recent reactions: the latest sequence number (the
/// cursor to send back on the next poll) and the events that occurred since the
/// cursor the presenter last sent.
/// </summary>
[GenerateSerializer]
public sealed record ReactionFeed(
    [property: Id(0)] long LastSeq,
    [property: Id(1)] ReactionEvent[] Events);

/// <summary>
/// The single global reaction feed. Attendees push a reaction each time they tap
/// a button; the presenter polls <see cref="GetSince"/> with the last sequence it
/// has rendered and animates each new event. Reactions are transient and
/// fire-and-forget, so this grain keeps a small, time-bounded buffer in memory
/// only — nothing needs to survive a silo restart.
/// </summary>
public interface IReactionsGrain : IGrainWithStringKey
{
    /// <summary>The well-known key of the one global reaction feed grain.</summary>
    public const string GlobalKey = "global";

    /// <summary>
    /// The reaction kinds the UI offers. Pushes of anything else are rejected so
    /// the feed can't be used to inject arbitrary content onto the presenter view.
    /// </summary>
    public static readonly IReadOnlySet<string> AllowedKinds =
        new HashSet<string>(StringComparer.Ordinal) { "heart", "thumbs", "question" };

    /// <summary>
    /// Records one reaction press of the given kind. Unknown kinds are ignored.
    /// </summary>
    Task Push(string kind);

    /// <summary>
    /// Returns reactions newer than <paramref name="afterSeq"/>. Pass null on the
    /// first poll to receive only the current cursor (no backlog), so a presenter
    /// joining mid-talk doesn't get a burst of stale reactions.
    /// </summary>
    Task<ReactionFeed> GetSince(long? afterSeq);
}
