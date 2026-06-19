using App.Api.GrainContracts;
using Orleans.TestingHost;

namespace App.Api.Tests;

/// <summary>
/// End-to-end integration tests for the cluster observability pipeline running on
/// a real test silo: the outgoing call filter, the per-silo queue, the reporter
/// grain service, the cluster-wide recorder grain, and the activation inventory.
/// </summary>
[Collection(ClusterCollection.Name)]
public sealed class ClusterObservabilityTests
{
    private readonly TestCluster _cluster;

    public ClusterObservabilityTests(ClusterFixture fixture) => _cluster = fixture.Cluster;

    [Fact]
    public async Task ClusterCallRecorder_keeps_only_the_last_100_calls_in_order()
    {
        // A dedicated key gives this test an isolated rolling buffer.
        var recorder = _cluster.GrainFactory.GetGrain<IClusterCallRecorderGrain>(987_654);

        var records = Enumerable.Range(0, 150)
            .Select(i => new GrainCallRecord(
                DateTimeOffset.UtcNow, "siloA", "src", $"target/{i}", "IFace", "M", i, true))
            .ToArray();
        await recorder.Append(records);

        var recent = await recorder.GetRecent();

        Assert.Equal(100, recent.Count);
        Assert.Equal("target/50", recent[0].TargetGrainId); // oldest still kept
        Assert.Equal("target/149", recent[^1].TargetGrainId); // newest, last
    }

    [Fact]
    public async Task ActivationInventory_snapshots_active_grains_with_their_silo()
    {
        // Activate an application grain so it shows up in the snapshot.
        var presenterKey = $"bob-{Guid.NewGuid():N}";
        await _cluster.GrainFactory.GetGrain<IPresenterGrain>(presenterKey).Initialize("Bob");

        var inventory = _cluster.GrainFactory.GetGrain<IActivationInventoryGrain>(0);
        await inventory.Start();

        // The first snapshot is taken on the next timer tick (~100ms), so poll.
        var snapshot = await PollUntil(
            inventory.GetSnapshot,
            s => s.Any(g => g.GrainId == $"presenter/{presenterKey}"));

        var presenter = Assert.Single(snapshot, g => g.GrainId == $"presenter/{presenterKey}");
        Assert.Contains("PresenterGrain", presenter.GrainType);
        Assert.False(string.IsNullOrWhiteSpace(presenter.SiloAddress));

        // The inventory grain reports itself too.
        Assert.Contains(snapshot, g => g.GrainId == "activationinventory/0");
    }

    [Fact]
    public async Task CallTrace_pipeline_records_grain_to_grain_calls_and_excludes_its_own_plumbing()
    {
        var recorder = _cluster.GrainFactory.GetGrain<IClusterCallRecorderGrain>(0);

        // A real grain-to-grain call: PresenterGrain -> MultipleChoiceGrain.Configure.
        var presenterKey = $"bob-{Guid.NewGuid():N}";
        var presenter = _cluster.GrainFactory.GetGrain<IPresenterGrain>(presenterKey);
        await presenter.Initialize("Bob");
        var actionId = await presenter.CreateMultipleChoice("Lunch?", ["Pizza", "Sushi"]);

        // Wait for the reporter grain service to flush the queue (every 100ms).
        var recent = await PollUntil(
            recorder.GetRecent,
            calls => calls.Any(c => IsConfigureCall(c, presenterKey, actionId)));

        var call = Assert.Single(recent, c => IsConfigureCall(c, presenterKey, actionId));
        Assert.True(call.Success);
        Assert.Contains("IMultipleChoiceGrain", call.InterfaceName);
        Assert.False(string.IsNullOrWhiteSpace(call.ObservedOnSilo));

        // ShouldIgnore + suppression: the tracing must never trace itself.
        Assert.DoesNotContain(recent, c => c.InterfaceName.Contains("IClusterCallRecorderGrain"));
        Assert.DoesNotContain(recent, c => c.InterfaceName.Contains("IActivationInventoryGrain"));
        Assert.DoesNotContain(recent, c => c.InterfaceName.Contains("IManagementGrain"));
        Assert.DoesNotContain(recent, c => c.InterfaceName.StartsWith("Orleans."));

        // Client-originated calls (no source grain) are not recorded.
        Assert.DoesNotContain(recent, c => c.SourceGrainId is null);
    }

    private static bool IsConfigureCall(GrainCallRecord c, string presenterKey, string actionId) =>
        c.SourceGrainId == $"presenter/{presenterKey}" &&
        c.TargetGrainId == $"multiplechoice/{actionId}" &&
        c.MethodName == "Configure";

    private static async Task<T> PollUntil<T>(
        Func<Task<T>> get, Func<T, bool> predicate, int timeoutMs = 5_000)
    {
        var deadline = DateTime.UtcNow.AddMilliseconds(timeoutMs);
        var value = await get();
        while (!predicate(value) && DateTime.UtcNow < deadline)
        {
            await Task.Delay(100);
            value = await get();
        }

        return value;
    }
}
