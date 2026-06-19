using App.Api.GrainContracts;
using Orleans.TestingHost;

namespace App.Api.Tests;

[Collection(ClusterCollection.Name)]
public sealed class AttendeeGrainTests
{
    private readonly TestCluster _cluster;

    public AttendeeGrainTests(ClusterFixture fixture) => _cluster = fixture.Cluster;

    private IAttendeeGrain NewAttendee() =>
        _cluster.GrainFactory.GetGrain<IAttendeeGrain>($"alice-{Guid.NewGuid():N}");

    private IPresentationGrain Presentation =>
        _cluster.GrainFactory.GetGrain<IPresentationGrain>(IPresentationGrain.GlobalKey);

    // Creates a presenter, an action, makes it the focused (live) action, and
    // returns the action id. Tests in this collection run sequentially, so the
    // global focus we set here stays put for the duration of the test.
    private async Task<string> MakeLiveQuestion(string title, string[] options)
    {
        var presenter = _cluster.GrainFactory.GetGrain<IPresenterGrain>($"bob-{Guid.NewGuid():N}");
        await presenter.Initialize("Bob");
        var actionId = await presenter.CreateMultipleChoice(title, options);
        await presenter.SetActive(actionId);
        return actionId;
    }

    [Fact]
    public async Task GetState_returns_no_focus_when_nothing_is_live()
    {
        await Presentation.ClearFocus();
        var attendee = NewAttendee();
        await attendee.Initialize("Alice");

        var state = await attendee.GetState();

        Assert.Equal("Alice", state.Name);
        Assert.Null(state.Focus);
        Assert.Null(state.YourAnswer);
    }

    [Fact]
    public async Task Answer_returns_false_when_nothing_is_live()
    {
        await Presentation.ClearFocus();
        var attendee = NewAttendee();
        await attendee.Initialize("Alice");

        Assert.False(await attendee.Answer(0));
    }

    [Fact]
    public async Task Answer_records_against_the_live_action_and_GetState_reflects_it()
    {
        var actionId = await MakeLiveQuestion("Lunch?", ["Pizza", "Sushi"]);
        var attendee = NewAttendee();
        await attendee.Initialize("Alice");

        Assert.True(await attendee.Answer(1));

        var state = await attendee.GetState();
        Assert.NotNull(state.Focus);
        Assert.Equal(actionId, state.Focus!.ActionId);
        Assert.Equal("Lunch?", state.Focus.Title);
        Assert.Equal(1, state.YourAnswer);

        // And it landed on the action grain's tally.
        var results = await _cluster.GrainFactory.GetGrain<IMultipleChoiceGrain>(actionId).GetResults();
        Assert.Equal(new[] { 0, 1 }, results.Counts);
    }

    [Fact]
    public async Task Answer_can_be_changed_while_action_is_live()
    {
        var actionId = await MakeLiveQuestion("Lunch?", ["Pizza", "Sushi"]);
        var attendee = NewAttendee();
        await attendee.Initialize("Alice");

        await attendee.Answer(0);
        await attendee.Answer(1); // change while live

        Assert.Equal(1, (await attendee.GetState()).YourAnswer);
        var results = await _cluster.GrainFactory.GetGrain<IMultipleChoiceGrain>(actionId).GetResults();
        Assert.Equal(new[] { 0, 1 }, results.Counts);
        Assert.Equal(1, results.Total);
    }
}
