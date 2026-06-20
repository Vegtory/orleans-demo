namespace App.Api.GrainContracts;

/// <summary>
/// One per attendee, keyed "action-{actionId}/attendee-{attendeeId}". The
/// attendee's control surface for ChargerSim: it owns charger creation, batch
/// commands, single-charger commands and kill, enforcing the 10,000 cap and that
/// the action is active. Fleet reads are delegated to the aggregate grain.
///
/// Creation and batch commands are accepted instantly and carried out by a
/// background worker (a grain timer): the caller records a request, and the grain
/// reconciles the fleet toward it over subsequent ticks. <see cref="GetWorkStatus"/>
/// exposes how much background work is still outstanding.
/// </summary>
public interface IAttendeeChargerSimGrain : IGrainWithStringKey
{
    public const int MaxChargers = 5_000;
    public const int DefaultBatchSize = 100;

    /// <summary>Idempotently records the attendee's display name and registers them with the action grain.</summary>
    Task Register(string displayName);

    /// <summary>
    /// Requests up to <paramref name="amount"/> more chargers (capped so the live
    /// fleet never exceeds <see cref="MaxChargers"/>). Returns immediately; the
    /// background worker creates them in chunks. The return value is the projected
    /// live fleet total once the request is fulfilled.
    /// </summary>
    Task<int> CreateChargers(int amount);

    /// <summary>Queues a "kill every live charger" request, drained by the background worker.</summary>
    Task KillMyChargers();

    /// <summary>
    /// Marks this sim as killed, then unregisters any reminders, wipes persisted
    /// state, and schedules the grain for deactivation. Subsequent calls to any
    /// method short-circuit through the same teardown path.
    /// </summary>
    Task Kill();

    /// <summary>Queues a batch command against up to <paramref name="amount"/> chargers; the background worker executes it.</summary>
    Task SendBatchCommand(BatchChargerCommandType command, int amount);

    /// <summary>Outstanding background work: chargers still to create and batch commands still queued.</summary>
    Task<ChargerSimWorkStatus> GetWorkStatus();

    /// <summary>Validates ownership + active, runs a command against one charger, and returns its fresh snapshot.</summary>
    Task<ChargerSnapshot?> CommandCharger(string chargerId, SingleChargerCommandType command);

    Task<ChargerFleetSummary> GetSummary();

    Task<ChargerSnapshot?> GetCharger(string chargerId);
    Task<ChargerSnapshot?> GetRandomActiveCharger();
    Task<ChargerSnapshot?> GetRandomPausedCharger();

    Task<IReadOnlyList<string>> GetChargerIds();
}
