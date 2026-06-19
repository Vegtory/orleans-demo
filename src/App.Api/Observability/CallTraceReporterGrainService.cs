using App.Api.GrainContracts;
using Orleans.Concurrency;
using Orleans.Runtime;
using Orleans.Services;

namespace App.Api.Observability;

/// <summary>Marker interface for the per-silo call-trace reporter grain service.</summary>
public interface ICallTraceReporterGrainService : IGrainService
{
}

/// <summary>
/// Runs on every silo for the lifetime of the silo. Every 100ms it drains the
/// local call-trace queue and forwards the batch to the cluster-wide
/// <see cref="IClusterCallRecorderGrain"/>. Using a batched reporter (instead of
/// reporting on every grain call) keeps the hot path cheap.
/// </summary>
[Reentrant]
public sealed class CallTraceReporterGrainService : GrainService, ICallTraceReporterGrainService
{
    private static readonly TimeSpan FlushInterval = TimeSpan.FromMilliseconds(100);

    private readonly IGrainFactory _grainFactory;
    private readonly LocalCallTraceQueue _queue;
    private readonly CallTraceSuppression _suppression;
    private readonly ILogger<CallTraceReporterGrainService> _logger;

    private PeriodicTimer? _timer;
    private CancellationTokenSource? _cts;
    private Task? _loop;

    public CallTraceReporterGrainService(
        GrainId id,
        Silo silo,
        ILoggerFactory loggerFactory,
        IGrainFactory grainFactory,
        LocalCallTraceQueue queue,
        CallTraceSuppression suppression)
        : base(id, silo, loggerFactory)
    {
        _grainFactory = grainFactory;
        _queue = queue;
        _suppression = suppression;
        _logger = loggerFactory.CreateLogger<CallTraceReporterGrainService>();
    }

    public override Task Start()
    {
        _cts = new CancellationTokenSource();
        _timer = new PeriodicTimer(FlushInterval);
        _loop = RunAsync(_cts.Token);
        return base.Start();
    }

    public override async Task Stop()
    {
        if (_cts is not null)
        {
            await _cts.CancelAsync();
        }

        _timer?.Dispose();

        if (_loop is not null)
        {
            try
            {
                await _loop;
            }
            catch (OperationCanceledException)
            {
                // Expected during shutdown.
            }
        }

        await base.Stop();
    }

    private async Task RunAsync(CancellationToken cancellationToken)
    {
        var recorder = _grainFactory.GetGrain<IClusterCallRecorderGrain>(0);

        try
        {
            while (_timer is not null && await _timer.WaitForNextTickAsync(cancellationToken))
            {
                var batch = _queue.Drain();
                if (batch.Count == 0)
                {
                    continue;
                }

                try
                {
                    // Suppress so this reporting call is not itself recorded.
                    using (_suppression.Suppress())
                    {
                        await recorder.Append(batch);
                    }
                }
                catch (Exception ex)
                {
                    _logger.LogWarning(ex, "Failed to report Orleans call trace batch.");
                }
            }
        }
        catch (OperationCanceledException)
        {
            // Expected during shutdown.
        }
    }
}
