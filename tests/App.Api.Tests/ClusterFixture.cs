using App.Api.Observability;
using Microsoft.Extensions.DependencyInjection;
using Orleans.TestingHost;

namespace App.Api.Tests;

/// <summary>
/// Configures each in-process test silo. The grains use a persistence provider
/// named "store"; here it is backed by in-memory storage (mirroring Local mode).
/// The cluster observability pipeline (call filter + per-silo queue + reporter
/// grain service) is registered too, mirroring Program.cs, so it can be
/// integration-tested end to end.
/// </summary>
public sealed class TestSiloConfigurator : ISiloConfigurator
{
    public void Configure(ISiloBuilder siloBuilder)
    {
        siloBuilder.AddMemoryGrainStorage("store");

        siloBuilder.Services.AddSingleton<LocalCallTraceQueue>();
        siloBuilder.Services.AddSingleton<CallTraceSuppression>();
        siloBuilder.Services.AddSingleton<CallTraceRuntimeSwitch>();
        siloBuilder.AddOutgoingGrainCallFilter<GrainCallTraceFilter>();
        siloBuilder.AddGrainService<CallTraceReporterGrainService>();
    }
}

/// <summary>
/// Boots a single shared <see cref="TestCluster"/> for the whole test collection,
/// so grain activation/persistence/cross-grain calls run against a real silo.
/// </summary>
public sealed class ClusterFixture : IDisposable
{
    public TestCluster Cluster { get; }

    public ClusterFixture()
    {
        var builder = new TestClusterBuilder();
        builder.AddSiloBuilderConfigurator<TestSiloConfigurator>();
        Cluster = builder.Build();
        Cluster.Deploy();
    }

    public void Dispose() => Cluster.StopAllSilos();
}

[CollectionDefinition(ClusterCollection.Name)]
public sealed class ClusterCollection : ICollectionFixture<ClusterFixture>
{
    public const string Name = "Cluster collection";
}
