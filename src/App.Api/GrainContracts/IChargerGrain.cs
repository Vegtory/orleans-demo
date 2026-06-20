namespace App.Api.GrainContracts;

/// <summary>
/// One simulated charger. The grain key is
/// "action-{actionId}/attendee-{attendeeId}/charger-{number}". Each charger owns
/// a 30-second Orleans reminder that drives its own randomized simulation, and
/// after every tick or command it publishes its absolute current contribution to
/// its attendee's aggregate grain.
/// </summary>
public interface IChargerGrain : IGrainWithStringKey
{
    /// <summary>
    /// Idempotently brings the charger to life: assigns its display id + max
    /// power, registers the 30-second reminder, and publishes an initial
    /// contribution. Safe to call again on an existing charger.
    /// </summary>
    Task Initialize(string displayId);

    Task<ChargerSnapshot> GetSnapshot();

    Task StartSession();
    Task StopCharging();
    Task ResumeCharging();
    Task StopSession();
    Task LowerPowerUsage();
    Task RandomChaos();
    Task Kill();
}
