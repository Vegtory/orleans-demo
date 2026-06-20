using App.Api.GrainContracts;
using Orleans.TestingHost;

namespace App.Api.Tests;

[Collection(ClusterCollection.Name)]
public sealed class ReactionsGrainTests
{
    private readonly TestCluster _cluster;

    public ReactionsGrainTests(ClusterFixture fixture) => _cluster = fixture.Cluster;

    // Each test uses its own grain key so the shared cluster's grains don't bleed
    // sequence state across tests.
    private IReactionsGrain Reactions(string key) =>
        _cluster.GrainFactory.GetGrain<IReactionsGrain>(key);

    [Fact]
    public async Task First_poll_without_a_cursor_returns_no_backlog()
    {
        var reactions = Reactions($"feed-{Guid.NewGuid():N}");
        await reactions.Push("heart");
        await reactions.Push("thumbs");

        var feed = await reactions.GetSince(null);

        Assert.Empty(feed.Events);
        Assert.Equal(2, feed.LastSeq);
    }

    [Fact]
    public async Task GetSince_returns_each_press_after_the_cursor()
    {
        var reactions = Reactions($"feed-{Guid.NewGuid():N}");
        var start = (await reactions.GetSince(null)).LastSeq;

        await reactions.Push("heart");
        await reactions.Push("heart");
        await reactions.Push("question");

        var feed = await reactions.GetSince(start);

        Assert.Equal(new[] { "heart", "heart", "question" }, feed.Events.Select(e => e.Kind));
        Assert.Equal(start + 3, feed.LastSeq);
    }

    [Fact]
    public async Task Advancing_the_cursor_drops_already_seen_events()
    {
        var reactions = Reactions($"feed-{Guid.NewGuid():N}");
        var start = (await reactions.GetSince(null)).LastSeq;

        await reactions.Push("thumbs");
        var first = await reactions.GetSince(start);

        await reactions.Push("heart");
        var second = await reactions.GetSince(first.LastSeq);

        Assert.Equal("heart", Assert.Single(second.Events).Kind);
    }

    [Fact]
    public async Task Unknown_kinds_are_ignored()
    {
        var reactions = Reactions($"feed-{Guid.NewGuid():N}");
        var start = (await reactions.GetSince(null)).LastSeq;

        await reactions.Push("rocket");

        var feed = await reactions.GetSince(start);
        Assert.Empty(feed.Events);
        Assert.Equal(start, feed.LastSeq);
    }
}
