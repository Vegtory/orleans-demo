using App.Api.GrainContracts;
using Orleans.Runtime;

namespace App.Api.Grains;

/// <summary>
/// Polls <see cref="IManagementGrain"/> on a timer and keeps a snapshot of every
/// active activation in the cluster together with the silo it lives on. The
/// presenter view reads <see cref="GetSnapshot"/> to render grains in their silos.
/// </summary>
public sealed class ActivationInventoryGrain : Grain, IActivationInventoryGrain
{
    private static readonly TimeSpan PollInterval = TimeSpan.FromMilliseconds(100);

    private IGrainTimer? _timer;
    private IReadOnlyList<ActiveGrainRecord> _snapshot = [];

    public Task Start()
    {
        _timer ??= this.RegisterGrainTimer(
            callback: static (self, _) => self.Refresh(),
            state: this,
            options: new GrainTimerCreationOptions
            {
                DueTime = TimeSpan.Zero,
                Period = PollInterval,
                Interleave = true
            });

        return Task.CompletedTask;
    }

    public Task<IReadOnlyList<ActiveGrainRecord>> GetSnapshot() => Task.FromResult(_snapshot);

    private async Task Refresh()
    {
        var management = GrainFactory.GetGrain<IManagementGrain>(0);
        var stats = await management.GetDetailedGrainStatistics();

        _snapshot = stats
            .Select(x => new ActiveGrainRecord(
                GrainId: x.GrainId.ToString(),
                GrainType: x.GrainType.ToString(),
                SiloAddress: x.SiloAddress.ToString()))
            .OrderBy(x => x.SiloAddress, StringComparer.Ordinal)
            .ThenBy(x => x.GrainType, StringComparer.Ordinal)
            .ThenBy(x => x.GrainId, StringComparer.Ordinal)
            .ToArray();
    }
}
