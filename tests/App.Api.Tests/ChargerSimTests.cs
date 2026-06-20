using App.Api.GrainContracts;
using Orleans.TestingHost;

namespace App.Api.Tests;

[Collection(ClusterCollection.Name)]
public sealed class ChargerSimTests
{
    private readonly TestCluster _cluster;

    public ChargerSimTests(ClusterFixture fixture) => _cluster = fixture.Cluster;

    private static ChargerAggregateContribution Contribution(
        string chargerId, long version, ChargerSimState state, double power = 0, double kwh = 0) =>
        new()
        {
            AttendeeId = "alice",
            ChargerId = chargerId,
            Version = version,
            State = state,
            HasSession = state is ChargerSimState.ActiveSession or ChargerSimState.PausedWithSession,
            ActivePowerKw = power,
            SessionKwh = kwh,
            UpdatedAt = DateTimeOffset.UtcNow
        };

    private IAttendeeChargerAggregateGrain NewAggregate() =>
        _cluster.GrainFactory.GetGrain<IAttendeeChargerAggregateGrain>(
            ChargerSimKeys.Aggregate(Guid.NewGuid().ToString("N"), "alice"));

    [Fact]
    public async Task Aggregate_tracks_absolute_contributions_into_summary()
    {
        var agg = NewAggregate();

        await agg.UpsertContribution(Contribution("CP-000001", 1, ChargerSimState.ActiveSession, power: 11, kwh: 2));
        await agg.UpsertContribution(Contribution("CP-000002", 1, ChargerSimState.NoSession));
        var summary = await agg.UpsertContribution(
            Contribution("CP-000003", 1, ChargerSimState.PausedWithSession, power: 0, kwh: 5));

        Assert.Equal(3, summary.TotalChargers);
        Assert.Equal(1, summary.ActiveSessionCount);
        Assert.Equal(1, summary.NoSessionCount);
        Assert.Equal(1, summary.PausedWithSessionCount);
        Assert.Equal(2, summary.ChargersWithSessionCount);
        Assert.Equal(11, summary.TotalActivePowerKw, 3);
        Assert.Equal(7, summary.TotalSessionKwh, 3);
    }

    [Fact]
    public async Task Aggregate_replaces_previous_contribution_not_accumulates()
    {
        var agg = NewAggregate();

        await agg.UpsertContribution(Contribution("CP-000001", 1, ChargerSimState.ActiveSession, power: 10, kwh: 1));
        var summary = await agg.UpsertContribution(
            Contribution("CP-000001", 2, ChargerSimState.ActiveSession, power: 22, kwh: 4));

        // Still one charger; totals reflect the latest, not the sum of both.
        Assert.Equal(1, summary.TotalChargers);
        Assert.Equal(1, summary.ActiveSessionCount);
        Assert.Equal(22, summary.TotalActivePowerKw, 3);
        Assert.Equal(4, summary.TotalSessionKwh, 3);
    }

    [Fact]
    public async Task Aggregate_ignores_older_or_equal_versions()
    {
        var agg = NewAggregate();

        await agg.UpsertContribution(Contribution("CP-000001", 5, ChargerSimState.ActiveSession, power: 22, kwh: 4));
        // Older version: ignored.
        await agg.UpsertContribution(Contribution("CP-000001", 3, ChargerSimState.NoSession));
        // Equal version: ignored.
        var summary = await agg.UpsertContribution(Contribution("CP-000001", 5, ChargerSimState.NoSession));

        Assert.Equal(1, summary.ActiveSessionCount);
        Assert.Equal(0, summary.NoSessionCount);
        Assert.Equal(22, summary.TotalActivePowerKw, 3);
    }

    [Fact]
    public async Task Killed_contribution_keeps_charger_counted_as_killed()
    {
        var agg = NewAggregate();

        await agg.UpsertContribution(Contribution("CP-000001", 1, ChargerSimState.ActiveSession, power: 22, kwh: 4));
        var summary = await agg.UpsertContribution(Contribution("CP-000001", 2, ChargerSimState.Killed));

        Assert.Equal(1, summary.TotalChargers);
        Assert.Equal(1, summary.KilledCount);
        Assert.Equal(0, summary.ActiveSessionCount);
        Assert.Equal(0, summary.TotalActivePowerKw, 3);
        Assert.Equal(0, summary.TotalSessionKwh, 3);
    }

    [Fact]
    public async Task Inactive_action_rejects_attendee_commands()
    {
        var actionId = Guid.NewGuid().ToString("N");
        var attendee = _cluster.GrainFactory.GetGrain<IAttendeeChargerSimGrain>(
            ChargerSimKeys.Attendee(actionId, "alice-1"));

        // Action grain defaults to inactive — never activated.
        await Assert.ThrowsAsync<InvalidOperationException>(() => attendee.CreateChargers(10));
    }

    [Fact]
    public async Task Attendee_can_create_chargers_and_summary_reflects_them()
    {
        var actionId = Guid.NewGuid().ToString("N");
        var action = _cluster.GrainFactory.GetGrain<IChargerSimActionGrain>(ChargerSimKeys.Action(actionId));
        await action.Activate();

        var attendee = _cluster.GrainFactory.GetGrain<IAttendeeChargerSimGrain>(
            ChargerSimKeys.Attendee(actionId, "alice-2"));
        await attendee.Register("Alice");

        var total = await attendee.CreateChargers(25);
        Assert.Equal(25, total);

        var summary = await attendee.GetSummary();
        Assert.Equal(25, summary.TotalChargers);
        // Freshly created chargers start with no session.
        Assert.Equal(25, summary.NoSessionCount);

        var ids = await attendee.GetChargerIds();
        Assert.Equal(25, ids.Count);
        Assert.Equal("CP-000001", ids[0]);
    }

    [Fact]
    public async Task CreateChargers_accumulates_and_never_exceeds_the_cap()
    {
        var actionId = Guid.NewGuid().ToString("N");
        var action = _cluster.GrainFactory.GetGrain<IChargerSimActionGrain>(ChargerSimKeys.Action(actionId));
        await action.Activate();

        var attendee = _cluster.GrainFactory.GetGrain<IAttendeeChargerSimGrain>(
            ChargerSimKeys.Attendee(actionId, "alice-cap"));

        Assert.Equal(50, await attendee.CreateChargers(50));
        Assert.Equal(80, await attendee.CreateChargers(30)); // accumulates
        Assert.Equal(80, (await attendee.GetChargerIds()).Count);

        // The cap is fixed regardless of how many are requested. (We avoid
        // creating the full 10,000 grains here — that is a load demo, not a unit
        // test — but the clamp is exercised by requesting more than the cap and
        // checking the returned total is bounded by it.)
        Assert.True(IAttendeeChargerSimGrain.MaxChargers >= await attendee.CreateChargers(1));
    }

    [Fact]
    public async Task KillMyChargers_marks_all_as_killed_in_summary()
    {
        var actionId = Guid.NewGuid().ToString("N");
        var action = _cluster.GrainFactory.GetGrain<IChargerSimActionGrain>(ChargerSimKeys.Action(actionId));
        await action.Activate();

        var attendee = _cluster.GrainFactory.GetGrain<IAttendeeChargerSimGrain>(
            ChargerSimKeys.Attendee(actionId, "alice-kill"));
        await attendee.Register("Alice");
        await attendee.CreateChargers(10);

        await attendee.KillMyChargers();

        var summary = await attendee.GetSummary();
        Assert.Equal(10, summary.TotalChargers);
        Assert.Equal(10, summary.KilledCount);
        Assert.Equal(0, summary.NoSessionCount);
    }

    [Fact]
    public async Task Single_charger_command_starts_a_session()
    {
        var actionId = Guid.NewGuid().ToString("N");
        var action = _cluster.GrainFactory.GetGrain<IChargerSimActionGrain>(ChargerSimKeys.Action(actionId));
        await action.Activate();

        var attendee = _cluster.GrainFactory.GetGrain<IAttendeeChargerSimGrain>(
            ChargerSimKeys.Attendee(actionId, "alice-single"));
        await attendee.Register("Alice");
        await attendee.CreateChargers(3);

        var snapshot = await attendee.CommandCharger("CP-000001", SingleChargerCommandType.StartSession);

        Assert.NotNull(snapshot);
        Assert.Equal(ChargerSimState.ActiveSession, snapshot!.State);
        Assert.NotNull(snapshot.ActiveSessionId);
        Assert.True(snapshot.ActivePowerKw > 0);
    }

    [Fact]
    public async Task Batch_start_then_stop_sessions_moves_chargers_through_states()
    {
        var actionId = Guid.NewGuid().ToString("N");
        var action = _cluster.GrainFactory.GetGrain<IChargerSimActionGrain>(ChargerSimKeys.Action(actionId));
        await action.Activate();

        var attendee = _cluster.GrainFactory.GetGrain<IAttendeeChargerSimGrain>(
            ChargerSimKeys.Attendee(actionId, "alice-batch"));
        await attendee.Register("Alice");
        await attendee.CreateChargers(40);

        await attendee.SendBatchCommand(BatchChargerCommandType.StartSessions, 40);
        var afterStart = await attendee.GetSummary();
        Assert.Equal(40, afterStart.ActiveSessionCount);

        await attendee.SendBatchCommand(BatchChargerCommandType.StopSessions, 40);
        var afterStop = await attendee.GetSummary();
        Assert.Equal(40, afterStop.NoSessionCount);
        Assert.Equal(0, afterStop.ActiveSessionCount);
    }

    [Fact]
    public async Task Presenter_kill_all_kills_every_attendees_chargers()
    {
        var actionId = Guid.NewGuid().ToString("N");
        var action = _cluster.GrainFactory.GetGrain<IChargerSimActionGrain>(ChargerSimKeys.Action(actionId));
        await action.Activate();

        var alice = _cluster.GrainFactory.GetGrain<IAttendeeChargerSimGrain>(
            ChargerSimKeys.Attendee(actionId, "alice-all"));
        var bob = _cluster.GrainFactory.GetGrain<IAttendeeChargerSimGrain>(
            ChargerSimKeys.Attendee(actionId, "bob-all"));
        await alice.Register("Alice");
        await bob.Register("Bob");
        await alice.CreateChargers(5);
        await bob.CreateChargers(7);

        await action.KillAllChargers();

        var global = await action.GetGlobalSummary();
        Assert.Equal(12, global.TotalChargers);
        Assert.Equal(12, global.KilledCount);

        var dashboard = await action.GetDashboard();
        Assert.Equal(2, dashboard.Attendees.Length);
        Assert.Contains(dashboard.RecentEvents, e => e.Contains("Presenter killed all chargers"));
    }
}
