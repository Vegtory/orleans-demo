namespace App.Api.GrainContracts;

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

    /// <summary>Returns up to <paramref name="take"/> charger ids whose last contribution matches the filter.</summary>
    Task<IReadOnlyList<string>> SelectChargerIds(ChargerSelectionFilter filter, int take);

    /// <summary>A random charger id whose last contribution is in the given state, or null if none.</summary>
    Task<string?> GetRandomChargerInState(ChargerSimState state);

    Task Reset();
}
