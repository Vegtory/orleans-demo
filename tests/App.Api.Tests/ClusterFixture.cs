using Orleans.TestingHost;

namespace App.Api.Tests;

/// <summary>
/// Configures each in-process test silo. The grains use a persistence provider
/// named "store"; here it is backed by in-memory storage (mirroring Local mode).
/// </summary>
public sealed class TestSiloConfigurator : ISiloConfigurator
{
    public void Configure(ISiloBuilder siloBuilder)
    {
        siloBuilder.AddMemoryGrainStorage("store");
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
