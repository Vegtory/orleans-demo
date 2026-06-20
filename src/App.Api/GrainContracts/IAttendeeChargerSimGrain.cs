namespace App.Api.GrainContracts;

/// <summary>
/// One per attendee, keyed "action-{actionId}/attendee-{attendeeId}". The
/// attendee's control surface for ChargerSim: it owns charger creation, batch
/// commands, single-charger commands and kill, enforcing the 10,000 cap and that
/// the action is active. Fleet reads are delegated to the aggregate grain.
/// </summary>
public interface IAttendeeChargerSimGrain : IGrainWithStringKey
{
    public const int MaxChargers = 10_000;
    public const int DefaultBatchSize = 1_000;

    /// <summary>Idempotently records the attendee's display name and registers them with the action grain.</summary>
    Task Register(string displayName);

    /// <summary>Creates up to <paramref name="amount"/> new chargers, capped at <see cref="MaxChargers"/> total. Returns the new total.</summary>
    Task<int> CreateChargers(int amount);

    Task KillMyChargers();

    Task SendBatchCommand(BatchChargerCommandType command, int amount);

    /// <summary>Validates ownership + active, runs a command against one charger, and returns its fresh snapshot.</summary>
    Task<ChargerSnapshot?> CommandCharger(string chargerId, SingleChargerCommandType command);

    Task<ChargerFleetSummary> GetSummary();

    Task<ChargerSnapshot?> GetCharger(string chargerId);
    Task<ChargerSnapshot?> GetRandomActiveCharger();
    Task<ChargerSnapshot?> GetRandomPausedCharger();

    Task<IReadOnlyList<string>> GetChargerIds();
}
