namespace App.Api.GrainContracts;

using Orleans.Concurrency;

/// <summary>
/// One aggregate grain per attendee, keyed
/// "action-{actionId}/attendee-{attendeeId}/aggregate". It stores the last known
/// contribution per charger and maintains a running <see cref="ChargerFleetSummary"/>
/// incrementally — it never polls the individual charger grains.
/// </summary>
public interface IAttendeeChargerAggregateGrain : IGrainWithStringKey
{
    /// <summary>
    /// Applies one charger's absolute contribution. Ignores updates whose version
    /// is older than or equal to the last known version for that charger, making
    /// the aggregate idempotent and order-independent. Returns the updated summary.
    /// </summary>
    Task<ChargerFleetSummary> UpsertContribution(ChargerAggregateContribution contribution);

    Task<ChargerFleetSummary> GetSummary();

    /// <summary>The most recently updated contributions, newest first (for dashboards).</summary>
    Task<IReadOnlyList<ChargerAggregateContribution>> GetRecentContributions(int take);

    /// <summary>
    /// A stable sample of up to <paramref name="take"/> chargers for the attendee's
    /// live fleet grid. Returned in stable insertion order (not by recency) so a cell
    /// keeps its position between polls and animates in place. Killed chargers remain
    /// in the sample so the grid shows them going dark.
    /// </summary>
    Task<IReadOnlyList<ChargerCellState>> GetStateSample(int take);

    /// <summary>Returns up to <paramref name="take"/> charger ids whose last contribution matches the filter.</summary>
    Task<IReadOnlyList<string>> SelectChargerIds(ChargerSelectionFilter filter, int take);

    /// <summary>A random charger id whose last contribution is in the given state, or null if none.</summary>
    Task<string?> GetRandomChargerInState(ChargerSimState state);

    /// <summary>
    /// Pushes the action's current kill-switch state into this aggregate's in-memory
    /// cache. The action grain calls this on every toggle so the hot
    /// <see cref="UpsertContribution"/> path reads a local flag instead of calling
    /// back into the (single, hot) action grain on every charger contribution.
    /// <see cref="AlwaysInterleaveAttribute"/> so the push lands promptly rather than
    /// queueing behind a backlog of contributions.
    /// </summary>
    [AlwaysInterleave]
    Task SetKillSwitch(bool enabled);

    Task Reset();
}
