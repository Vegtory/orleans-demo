using Orleans.Runtime;

namespace App.Api.GrainContracts;

// ---------------------------------------------------------------------------
// Debug/demo cluster observability.
//
// These types power the presenter's "cluster activity" visualization:
//   * GrainCallRecord    — one observed grain->grain call (for the moving lines)
//   * ActiveGrainRecord  — one active activation + the silo it lives on
//
// The data is collected entirely for demonstration and is intentionally
// best-effort (rolling buffers, periodic snapshots). None of it is persisted.
// ---------------------------------------------------------------------------

/// <summary>One observed outgoing grain-to-grain call.</summary>
[GenerateSerializer]
public sealed record GrainCallRecord(
    [property: Id(0)] DateTimeOffset TimestampUtc,
    [property: Id(1)] string ObservedOnSilo,
    [property: Id(2)] string? SourceGrainId,
    [property: Id(3)] string TargetGrainId,
    [property: Id(4)] string InterfaceName,
    [property: Id(5)] string MethodName,
    [property: Id(6)] long DurationMs,
    [property: Id(7)] bool Success);

/// <summary>One active grain activation and the silo it currently lives on.</summary>
[GenerateSerializer]
public sealed record ActiveGrainRecord(
    [property: Id(0)] string GrainId,
    [property: Id(1)] string GrainType,
    [property: Id(2)] string SiloAddress);

/// <summary>
/// The cluster-wide call-tracing toggle. <see cref="Version"/> increments only
/// when <see cref="Enabled"/> actually changes, so silos can cheaply detect when
/// they need to apply a new value.
/// </summary>
[GenerateSerializer]
public sealed record TraceToggleState(
    [property: Id(0)] bool Enabled,
    [property: Id(1)] long Version);

/// <summary>
/// Cluster-wide rolling recorder of the most recent grain calls. A single
/// well-known activation (key 0) keeps the last <c>N</c> calls reported by every
/// silo's <c>CallTraceReporterGrainService</c>.
/// </summary>
public interface IClusterCallRecorderGrain : IGrainWithIntegerKey
{
    Task Append(IReadOnlyList<GrainCallRecord> records);
    Task<IReadOnlyList<GrainCallRecord>> GetRecent();
}

/// <summary>
/// Periodically snapshots all active activations across the cluster (via
/// <see cref="IManagementGrain"/>) so the presenter view can render where grains
/// live. A single well-known activation (key 0) owns the polling timer.
/// </summary>
public interface IActivationInventoryGrain : IGrainWithIntegerKey
{
    /// <summary>Starts the periodic snapshot timer. Idempotent.</summary>
    Task Start();
    Task<IReadOnlyList<ActiveGrainRecord>> GetSnapshot();
}

/// <summary>
/// Cluster-wide owner of the call-tracing on/off toggle. A single well-known
/// activation (key 0) holds the authoritative value; each silo's reporter polls
/// it and updates its local <c>CallTraceRuntimeSwitch</c>.
/// </summary>
public interface IClusterTraceControlGrain : IGrainWithIntegerKey
{
    Task<TraceToggleState> GetState();
    Task SetEnabled(bool enabled);
}
