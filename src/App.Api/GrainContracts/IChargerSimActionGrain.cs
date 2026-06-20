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
    Task<bool> IsActive();

    Task RegisterAttendee(string attendeeId);

    Task<IReadOnlyList<ChargerFleetSummary>> GetAllAttendeeSummaries();
    Task<ChargerFleetSummary> GetGlobalSummary();

    /// <summary>Global + per-attendee summaries + recent event messages, in one call.</summary>
    Task<ChargerSimDashboard> GetDashboard();

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
