using Orleans.Concurrency;

namespace App.Api.GrainContracts;

/// <summary>
/// The top-level ChargerSim action, keyed "action-{actionId}". Tracks whether
/// the action is active, which attendees have joined, and aggregates per-attendee
/// fleet summaries (read from each attendee's aggregate grain — never from the
/// individual chargers) into a global view for the presenter dashboard.
/// </summary>
public interface IChargerSimActionGrain : IGrainWithStringKey
{
    Task Activate();
    Task Deactivate();

    // Read-only, non-awaiting flag reads. Marked AlwaysInterleave so they can run
    // re-entrantly: an attendee/aggregate grain pulls these from its OnActivateAsync,
    // and that activation can happen while this grain is mid-await inside the
    // dashboard fan-out (GetDashboard -> GetAllAttendeeSummaries -> ... -> activate).
    // Without interleaving, that re-entrant call would deadlock behind the fan-out.
    [AlwaysInterleave]
    Task<bool> IsActive();

    [AlwaysInterleave]
    Task<bool> IsKillSwitchEnabled();

    /// <summary>
    /// The presenter-configurable maximum number of live chargers each attendee may
    /// create. Defaults to <see cref="IAttendeeChargerSimGrain.DefaultMaxChargers"/>.
    /// Marked <see cref="AlwaysInterleaveAttribute"/> like the other flag reads so an
    /// attendee grain can pull it from inside its own command turn without deadlocking.
    /// </summary>
    [AlwaysInterleave]
    Task<int> GetMaxChargers();

    /// <summary>
    /// Sets the per-attendee charger cap, clamped to
    /// [1, <see cref="IAttendeeChargerSimGrain.MaxChargers"/>].
    /// </summary>
    Task SetMaxChargers(int maxChargers);

    Task RegisterAttendee(string attendeeId);

    Task<IReadOnlyList<ChargerFleetSummary>> GetAllAttendeeSummaries();
    Task<ChargerFleetSummary> GetGlobalSummary();

    /// <summary>Global + per-attendee summaries + recent event messages, in one call.</summary>
    Task<ChargerSimDashboard> GetDashboard();

    /// <summary>
    /// The per-attendee fleet summaries for an attendee-facing leaderboard. Served
    /// from the same cached dashboard snapshot as <see cref="GetDashboard"/>, so
    /// attendee polling never triggers more than one fan-out per second regardless
    /// of how many attendees poll. Callers rank the result client-side.
    /// </summary>
    Task<IReadOnlyList<ChargerFleetSummary>> GetLeaderboard();

    /// <summary>Sets the room-wide collaborative target total active power (kW); 0 clears it.</summary>
    Task SetGoal(double targetActivePowerKw);

    /// <summary>The current goal plus the fleet's live total active power, for the shared progress bar.</summary>
    Task<ChargerSimGoalStatus> GetGoalStatus();

    /// <summary>Sends a kill command to every registered attendee's controller grain.</summary>
    Task KillAllChargers();

    /// <summary>
    /// Persists the kill-switch state. When enabled, immediately kills all live
    /// charger grains and records the event. When disabled, clears the flag.
    /// </summary>
    Task SetKillSwitch(bool enabled);

    /// <summary>Kills all chargers then clears all persisted state for this action.</summary>
    Task Delete();

    /// <summary>
    /// Records a short, human-readable event message for the dashboard ticker.
    /// Marked <see cref="AlwaysInterleaveAttribute"/> so an attendee's controller
    /// can record its event while this grain is awaiting that same attendee inside
    /// <see cref="KillAllChargers"/> — otherwise that call chain would deadlock.
    /// </summary>
    [AlwaysInterleave]
    Task RecordEvent(string message);
}
