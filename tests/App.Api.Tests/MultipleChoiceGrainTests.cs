using App.Api.GrainContracts;
using Orleans.TestingHost;

namespace App.Api.Tests;

[Collection(ClusterCollection.Name)]
public sealed class MultipleChoiceGrainTests
{
    private readonly TestCluster _cluster;

    public MultipleChoiceGrainTests(ClusterFixture fixture) => _cluster = fixture.Cluster;

    private IMultipleChoiceGrain NewQuestion(out string id)
    {
        id = Guid.NewGuid().ToString("N");
        return _cluster.GrainFactory.GetGrain<IMultipleChoiceGrain>(id);
    }

    [Fact]
    public async Task Configure_then_GetQuestion_round_trips_title_and_options()
    {
        var grain = NewQuestion(out var id);
        await grain.Configure("Lunch?", ["Pizza", "Sushi", "Salad"]);

        var q = await grain.GetQuestion();

        Assert.Equal(id, q.ActionId);
        Assert.Equal("Lunch?", q.Title);
        Assert.Equal(["Pizza", "Sushi", "Salad"], q.Options);
    }

    [Fact]
    public async Task Answer_is_recorded_and_tallied()
    {
        var grain = NewQuestion(out _);
        await grain.Configure("Lunch?", ["Pizza", "Sushi"]);

        await grain.Answer("alice", 0); // pizza
        await grain.Answer("bob", 1);   // sushi
        await grain.Answer("carol", 1); // sushi

        var results = await grain.GetResults();
        Assert.Equal(new[] { 1, 2 }, results.Counts); // pizza=1, sushi=2
        Assert.Equal(3, results.Total);
    }

    [Fact]
    public async Task Answer_can_be_changed_and_does_not_double_count()
    {
        var grain = NewQuestion(out _);
        await grain.Configure("Lunch?", ["Pizza", "Sushi"]);

        await grain.Answer("alice", 0);
        await grain.Answer("alice", 1); // change of mind

        Assert.Equal(1, await grain.GetAnswer("alice"));
        var results = await grain.GetResults();
        Assert.Equal(new[] { 0, 1 }, results.Counts);
        Assert.Equal(1, results.Total);
    }

    [Fact]
    public async Task GetAnswer_is_null_for_unknown_attendee()
    {
        var grain = NewQuestion(out _);
        await grain.Configure("Lunch?", ["Pizza", "Sushi"]);

        Assert.Null(await grain.GetAnswer("nobody"));
    }

    [Theory]
    [InlineData(-1)]
    [InlineData(2)]
    [InlineData(99)]
    public async Task Answer_rejects_out_of_range_option(int badIndex)
    {
        var grain = NewQuestion(out _);
        await grain.Configure("Lunch?", ["Pizza", "Sushi"]);

        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(() => grain.Answer("alice", badIndex));
    }
}
