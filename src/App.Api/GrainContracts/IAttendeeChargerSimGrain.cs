namespace App.Api.GrainContracts;

/// <summary>
/// One per attendee, keyed "action-{actionId}/attendee-{attendeeId}". The
/// attendee's control surface for ChargerSim: it owns charger creation, batch
/// commands, single-charger commands and kill, enforcing the 10,000 cap and that
/// the action is active. Fleet reads are delegated to the aggregate grain.
/// </summary>
public interface IAttendeeChargerSimGrain : IGrainWithStringKey
{
    public const int MaxChargers = 5_000;
    public const int DefaultBatchSize = 100;

    /// <summary>Idempotently records the attendee's display name and registers them with the action grain.</summary>
    Task Register(string displayName);

    /// <summary>Creates up to <paramref name="amount"/> new chargers, capped at <see cref="MaxChargers"/> total. Returns the new total.</summary>
    Task<int> CreateChargers(int amount);

    Task KillMyChargers();

    /// <summary>
    /// Marks this sim as killed, then unregisters any reminders, wipes persisted
    /// state, and schedules the grain for deactivation. Subsequent calls to any
    /// method short-circuit through the same teardown path.
    /// </summary>
    Task Kill();

    Task SendBatchCommand(BatchChargerCommandType command, int amount);

    /// <summary>Validates ownership + active, runs a command against one charger, and returns its fresh snapshot.</summary>
    Task<ChargerSnapshot?> CommandCharger(string chargerId, SingleChargerCommandType command);

    Task<ChargerFleetSummary> GetSummary();

    Task<ChargerSnapshot?> GetCharger(string chargerId);
    Task<ChargerSnapshot?> GetRandomActiveCharger();
    Task<ChargerSnapshot?> GetRandomPausedCharger();

    Task<IReadOnlyList<string>> GetChargerIds();
}
