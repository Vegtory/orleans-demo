using App.Api.GrainContracts;
using Orleans.TestingHost;

namespace App.Api.Tests;

[Collection(ClusterCollection.Name)]
public sealed class PresentationGrainTests
{
    private readonly TestCluster _cluster;

    public PresentationGrainTests(ClusterFixture fixture) => _cluster = fixture.Cluster;

    [Fact]
    public async Task SetFocus_then_ClearFocus_updates_GetFocus()
    {
        var presentation = _cluster.GrainFactory.GetGrain<IPresentationGrain>(IPresentationGrain.GlobalKey);

        await presentation.SetFocus("action-123");
        Assert.Equal("action-123", await presentation.GetFocus());

        await presentation.ClearFocus();
        Assert.Null(await presentation.GetFocus());
    }
}
