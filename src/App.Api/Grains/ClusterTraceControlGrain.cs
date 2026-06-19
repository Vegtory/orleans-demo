using App.Api.GrainContracts;

namespace App.Api.Grains;

/// <summary>
/// Holds the cluster-wide call-tracing toggle. In-memory only — the presenter
/// flips it from the dashboard; every silo's reporter polls
/// <see cref="GetState"/> and applies changes locally. The version increments
/// only on a real change so silos can skip no-op updates.
/// </summary>
public sealed class ClusterTraceControlGrain : Grain, IClusterTraceControlGrain
{
    private bool _enabled = true;
    private long _version;

    public Task<TraceToggleState> GetState() =>
        Task.FromResult(new TraceToggleState(_enabled, _version));

    public Task SetEnabled(bool enabled)
    {
        if (_enabled != enabled)
        {
            _enabled = enabled;
            _version++;
        }

        return Task.CompletedTask;
    }
}
