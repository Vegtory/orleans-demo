using App.Api.GrainContracts;
using Orleans.TestingHost;

namespace App.Api.Tests;

[Collection(ClusterCollection.Name)]
public sealed class PresenterGrainTests
{
    private readonly TestCluster _cluster;

    public PresenterGrainTests(ClusterFixture fixture) => _cluster = fixture.Cluster;

    private IPresenterGrain NewPresenter() =>
        _cluster.GrainFactory.GetGrain<IPresenterGrain>($"bob-{Guid.NewGuid():N}");

    [Fact]
    public async Task Initialize_sets_name_and_keeps_it_on_reinit()
    {
        var presenter = NewPresenter();
        await presenter.Initialize("Bob");
        await presenter.Initialize("Someone Else"); // should be ignored

        var state = await presenter.GetState();
        Assert.Equal("Bob", state.Name);
        Assert.Empty(state.Actions);
        Assert.Null(state.ActiveActionId);
    }

    [Fact]
    public async Task CreateMultipleChoice_records_summary_and_configures_action()
    {
        var presenter = NewPresenter();
        await presenter.Initialize("Bob");

        var actionId = await presenter.CreateMultipleChoice("Lunch?", ["Pizza", "Sushi"]);

        var state = await presenter.GetState();
        var summary = Assert.Single(state.Actions);
        Assert.Equal(actionId, summary.Id);
        Assert.Equal("Lunch?", summary.Title);
        Assert.Equal(2, summary.OptionCount);

        // The underlying action grain was configured with the same content.
        var question = await _cluster.GrainFactory.GetGrain<IMultipleChoiceGrain>(actionId).GetQuestion();
        Assert.Equal("Lunch?", question.Title);
        Assert.Equal(["Pizza", "Sushi"], question.Options);
    }

    [Fact]
    public async Task SetActive_focuses_action_on_global_presentation()
    {
        var presenter = NewPresenter();
        await presenter.Initialize("Bob");
        var actionId = await presenter.CreateMultipleChoice("Lunch?", ["Pizza", "Sushi"]);

        await presenter.SetActive(actionId);

        Assert.Equal(actionId, (await presenter.GetState()).ActiveActionId);
        var focus = await _cluster.GrainFactory
            .GetGrain<IPresentationGrain>(IPresentationGrain.GlobalKey)
            .GetFocus();
        Assert.Equal(actionId, focus);
    }

    [Fact]
    public async Task SetActive_rejects_unknown_action()
    {
        var presenter = NewPresenter();
        await presenter.Initialize("Bob");

        await Assert.ThrowsAsync<InvalidOperationException>(() => presenter.SetActive("not-mine"));
    }

    [Fact]
    public async Task GetResults_rejects_unknown_action()
    {
        var presenter = NewPresenter();
        await presenter.Initialize("Bob");

        await Assert.ThrowsAsync<InvalidOperationException>(() => presenter.GetResults("not-mine"));
    }

    [Fact]
    public async Task GetResults_returns_tally_for_own_action()
    {
        var presenter = NewPresenter();
        await presenter.Initialize("Bob");
        var actionId = await presenter.CreateMultipleChoice("Lunch?", ["Pizza", "Sushi"]);
        await _cluster.GrainFactory.GetGrain<IMultipleChoiceGrain>(actionId).Answer("alice", 1);

        var results = await presenter.GetResults(actionId);

        Assert.Equal(new[] { 0, 1 }, results.Counts);
        Assert.Equal(1, results.Total);
    }
}
