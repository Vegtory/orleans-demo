namespace App.Api.Observability;

/// <summary>
/// Per-silo, in-memory on/off flag for call tracing. The outgoing call filter
/// reads it on the hot path (a single volatile read), so disabling tracing is
/// almost free. It is kept in sync with the cluster-wide
/// <c>IClusterTraceControlGrain</c> by the reporter grain service, which polls
/// the control grain every 100ms. Registered as a singleton per silo.
/// </summary>
public sealed class CallTraceRuntimeSwitch
{
    // Tracing is on by default until the first toggle poll says otherwise.
    private int _enabled = 1;

    public bool IsEnabled => Volatile.Read(ref _enabled) == 1;

    public void SetEnabled(bool enabled) => Volatile.Write(ref _enabled, enabled ? 1 : 0);
}
