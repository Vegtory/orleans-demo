using System.Diagnostics;
using App.Api.GrainContracts;
using Orleans.Runtime;

namespace App.Api.Observability;

/// <summary>
/// Outgoing grain call filter that records grain-to-grain calls into the per-silo
/// <see cref="LocalCallTraceQueue"/>. It deliberately ignores its own plumbing
/// (recorder/inventory/management/system calls) so the tracing system does not
/// trace itself.
/// </summary>
public sealed class GrainCallTraceFilter(
    LocalCallTraceQueue queue,
    CallTraceSuppression suppression,
    CallTraceRuntimeSwitch runtimeSwitch,
    ILocalSiloDetails siloDetails)
    : IOutgoingGrainCallFilter
{
    public async Task Invoke(IOutgoingGrainCallContext context)
    {
        // A single local volatile read when tracing is off — no grain call.
        if (!runtimeSwitch.IsEnabled || suppression.IsSuppressed || ShouldIgnore(context))
        {
            await context.Invoke();
            return;
        }

        var startedAt = DateTimeOffset.UtcNow;
        var sw = Stopwatch.StartNew();
        var success = false;
        try
        {
            await context.Invoke();
            success = true;
        }
        finally
        {
            sw.Stop();

            // Keep only grain-originated calls. SourceId is null for
            // client/system-originated calls, which we don't visualize.
            if (context.SourceId is { } sourceId)
            {
                queue.Enqueue(new GrainCallRecord(
                    TimestampUtc: startedAt,
                    ObservedOnSilo: siloDetails.SiloAddress.ToString(),
                    SourceGrainId: sourceId.ToString(),
                    TargetGrainId: context.TargetId.ToString(),
                    InterfaceName: context.InterfaceName,
                    MethodName: context.MethodName,
                    DurationMs: sw.ElapsedMilliseconds,
                    Success: success));
            }
        }
    }

    private static bool ShouldIgnore(IOutgoingGrainCallContext context)
    {
        var interfaceName = context.InterfaceName;
        return interfaceName.Contains(nameof(IClusterCallRecorderGrain), StringComparison.Ordinal)
            || interfaceName.Contains(nameof(IActivationInventoryGrain), StringComparison.Ordinal)
            || interfaceName.Contains(nameof(IClusterTraceControlGrain), StringComparison.Ordinal)
            || interfaceName.Contains("IManagementGrain", StringComparison.Ordinal)
            || interfaceName.StartsWith("Orleans.", StringComparison.Ordinal);
    }
}
