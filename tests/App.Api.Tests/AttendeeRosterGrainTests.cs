using App.Api.GrainContracts;
using Orleans.TestingHost;

namespace App.Api.Tests;

[Collection(ClusterCollection.Name)]
public sealed class AttendeeRosterGrainTests
{
    private readonly TestCluster _cluster;

    public AttendeeRosterGrainTests(ClusterFixture fixture) => _cluster = fixture.Cluster;

    private IAttendeeRosterGrain Roster =>
        _cluster.GrainFactory.GetGrain<IAttendeeRosterGrain>(IAttendeeRosterGrain.GlobalKey);

    [Fact]
    public async Task Heartbeat_makes_an_attendee_appear_on_the_active_roster()
    {
        var key = $"alice-{Guid.NewGuid():N}";
        await Roster.Heartbeat(key, "Alice");

        var roster = await Roster.GetActive();

        Assert.Contains(roster.Attendees, a => a.Name == "Alice");
        Assert.Equal(roster.Attendees.Length, roster.Count);
    }

    [Fact]
    public async Task Repeat_heartbeats_update_rather_than_duplicate_the_attendee()
    {
        var key = $"bob-{Guid.NewGuid():N}";
        await Roster.Heartbeat(key, "Bob");
        var first = (await Roster.GetActive()).Attendees.First(a => a.Name == "Bob").LastSeen;

        await Task.Delay(10);
        await Roster.Heartbeat(key, "Bob");
        var matches = (await Roster.GetActive()).Attendees.Where(a => a.Name == "Bob").ToList();

        Assert.Single(matches);
        Assert.True(matches[0].LastSeen >= first);
    }

    [Fact]
    public async Task Polling_an_attendee_grain_registers_it_on_the_roster()
    {
        var key = $"carol-{Guid.NewGuid():N}";
        var attendee = _cluster.GrainFactory.GetGrain<IAttendeeGrain>(key);
        await attendee.Initialize("Carol");

        // GetState is the attendee's poll, which doubles as the heartbeat.
        await attendee.GetState();

        var roster = await Roster.GetActive();
        Assert.Contains(roster.Attendees, a => a.Name == "Carol");
    }
}
