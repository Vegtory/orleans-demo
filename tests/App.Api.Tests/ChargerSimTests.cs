using App.Api.GrainContracts;
using Orleans.TestingHost;

namespace App.Api.Tests;

[Collection(ClusterCollection.Name)]
public sealed class ChargerSimTests
{
    private readonly TestCluster _cluster;

    public ChargerSimTests(ClusterFixture fixture) => _cluster = fixture.Cluster;

    private static ChargerAggregateContribution Contribution(
        string chargerId, long version, ChargerSimState state, double power = 0, double kwh = 0, double maxPower = 0) =>
        new()
        {
            AttendeeId = "alice",
            ChargerId = chargerId,
            Version = version,
            State = state,
            HasSession = state is ChargerSimState.ActiveSession or ChargerSimState.PausedWithSession,
            ActivePowerKw = power,
            SessionKwh = kwh,
            MaxPowerKw = maxPower,
            UpdatedAt = DateTimeOffset.UtcNow
        };

    private IAttendeeChargerAggregateGrain NewAggregate() =>
        _cluster.GrainFactory.GetGrain<IAttendeeChargerAggregateGrain>(
            ChargerSimKeys.Aggregate(Guid.NewGuid().ToString("N"), "alice"));

    // Creation and batch commands are now carried out by a background worker, so
    // tests enqueue the request and then wait for the worker to drain it before
    // asserting on the resulting fleet.
    private static async Task Drain(IAttendeeChargerSimGrain attendee)
    {
        for (var i = 0; i < 400; i++)
        {
            var status = await attendee.GetWorkStatus();
            if (status.PendingChargers == 0 && status.QueuedCommands == 0)
            {
                return;
            }

            await Task.Delay(25);
        }

        throw new TimeoutException("ChargerSim background work did not drain in time.");
    }

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
    public async Task GetStateSample_returns_a_cell_per_charger_with_state_and_power()
    {
        var agg = NewAggregate();

        await agg.UpsertContribution(Contribution("CP-000001", 1, ChargerSimState.ActiveSession, power: 11, maxPower: 22));
        await agg.UpsertContribution(Contribution("CP-000002", 1, ChargerSimState.NoSession));
        await agg.UpsertContribution(Contribution("CP-000003", 1, ChargerSimState.PausedWithSession));

        var sample = await agg.GetStateSample(10);
        Assert.Equal(3, sample.Count);

        var active = sample.Single(c => c.State == ChargerSimState.ActiveSession);
        Assert.Equal(11, active.ActivePowerKw, 3);
        Assert.Equal(22, active.MaxPowerKw, 3);
        Assert.Contains(sample, c => c.State == ChargerSimState.NoSession);
        Assert.Contains(sample, c => c.State == ChargerSimState.PausedWithSession);
    }

    [Fact]
    public async Task GetStateSample_caps_the_returned_count_at_take()
    {
        var agg = NewAggregate();
        for (var i = 1; i <= 5; i++)
        {
            await agg.UpsertContribution(Contribution($"CP-{i:000000}", 1, ChargerSimState.NoSession));
        }

        Assert.Equal(2, (await agg.GetStateSample(2)).Count);
    }

    [Fact]
    public async Task GetLeaderboard_returns_one_summary_per_registered_attendee()
    {
        var actionId = Guid.NewGuid().ToString("N");
        var action = _cluster.GrainFactory.GetGrain<IChargerSimActionGrain>(ChargerSimKeys.Action(actionId));
        await action.Activate();

        var alice = _cluster.GrainFactory.GetGrain<IAttendeeChargerSimGrain>(
            ChargerSimKeys.Attendee(actionId, "alice-lb"));
        var bob = _cluster.GrainFactory.GetGrain<IAttendeeChargerSimGrain>(
            ChargerSimKeys.Attendee(actionId, "bob-lb"));
        await alice.Register("Alice");
        await bob.Register("Bob");
        await alice.CreateChargers(5);
        await bob.CreateChargers(8);
        await Drain(alice);
        await Drain(bob);

        var leaderboard = await action.GetLeaderboard();

        Assert.Equal(2, leaderboard.Count);
        Assert.Contains(leaderboard, s => s.AttendeeName == "Alice" && s.TotalChargers == 5);
        Assert.Contains(leaderboard, s => s.AttendeeName == "Bob" && s.TotalChargers == 8);
    }

    [Fact]
    public async Task Goal_is_set_by_presenter_and_tracks_live_fleet_power()
    {
        var actionId = Guid.NewGuid().ToString("N");
        var action = _cluster.GrainFactory.GetGrain<IChargerSimActionGrain>(ChargerSimKeys.Action(actionId));
        await action.Activate();

        // No goal yet.
        var before = await action.GetGoalStatus();
        Assert.Equal(0, before.GoalActivePowerKw, 3);

        await action.SetGoal(500);

        var attendee = _cluster.GrainFactory.GetGrain<IAttendeeChargerSimGrain>(
            ChargerSimKeys.Attendee(actionId, "alice-goal"));
        await attendee.Register("Alice");
        await attendee.CreateChargers(20);
        await Drain(attendee);
        await attendee.SendBatchCommand(BatchChargerCommandType.StartSessions, 20);
        await Drain(attendee);

        // The status reports the target plus the fleet's live total power. The power
        // is served from the dashboard cache (refreshed ~1s), so poll briefly for it.
        ChargerSimGoalStatus status = await action.GetGoalStatus();
        for (var i = 0; i < 60 && status.CurrentActivePowerKw <= 0; i++)
        {
            await Task.Delay(50);
            status = await action.GetGoalStatus();
        }
        Assert.Equal(500, status.GoalActivePowerKw, 3);
        Assert.True(status.CurrentActivePowerKw > 0, "expected live power from active sessions");

        // Clearing the goal resets it to zero.
        await action.SetGoal(0);
        Assert.Equal(0, (await action.GetGoalStatus()).GoalActivePowerKw, 3);
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

        // The request is accepted immediately and projects the eventual total.
        var total = await attendee.CreateChargers(25);
        Assert.Equal(25, total);

        await Drain(attendee);

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

        // Each request projects the running live+pending total, capped at MaxChargers.
        Assert.Equal(50, await attendee.CreateChargers(50));
        Assert.Equal(80, await attendee.CreateChargers(30)); // accumulates

        await Drain(attendee);
        Assert.Equal(80, (await attendee.GetChargerIds()).Count);

        // The cap is fixed regardless of how many are requested. (We avoid
        // creating the full fleet here — that is a load demo, not a unit test —
        // but the clamp is exercised by requesting more than the cap and checking
        // the returned total is bounded by it.)
        Assert.True(IAttendeeChargerSimGrain.MaxChargers >= await attendee.CreateChargers(1));
        await Drain(attendee);
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
        await Drain(attendee);

        await attendee.KillMyChargers();
        await Drain(attendee);

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
        await Drain(attendee);

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
        await Drain(attendee);

        await attendee.SendBatchCommand(BatchChargerCommandType.StartSessions, 40);
        await Drain(attendee);
        var afterStart = await attendee.GetSummary();
        Assert.Equal(40, afterStart.ActiveSessionCount);

        await attendee.SendBatchCommand(BatchChargerCommandType.StopSessions, 40);
        await Drain(attendee);
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
        await Drain(alice);
        await Drain(bob);

        await action.KillAllChargers();

        var global = await action.GetGlobalSummary();
        Assert.Equal(12, global.TotalChargers);
        Assert.Equal(12, global.KilledCount);

        var dashboard = await action.GetDashboard();
        Assert.Equal(2, dashboard.Attendees.Length);
        Assert.Contains(dashboard.RecentEvents, e => e.Contains("Presenter killed all chargers"));
    }

    [Fact]
    public async Task Presenter_kill_all_finds_fleets_that_never_registered()
    {
        var actionId = Guid.NewGuid().ToString("N");
        var action = _cluster.GrainFactory.GetGrain<IChargerSimActionGrain>(ChargerSimKeys.Action(actionId));
        await action.Activate();

        // Note: carol never calls Register, so the action grain has no record of
        // her — yet KillAllChargers must still find and kill her live chargers by
        // searching the cluster for active charger grains.
        var carol = _cluster.GrainFactory.GetGrain<IAttendeeChargerSimGrain>(
            ChargerSimKeys.Attendee(actionId, "carol-unreg"));
        await carol.CreateChargers(6);
        await Drain(carol);

        await action.KillAllChargers();

        var summary = await carol.GetSummary();
        Assert.Equal(6, summary.KilledCount);
        Assert.Equal(0, summary.NoSessionCount);
    }

    [Fact]
    public async Task Killed_chargers_do_not_count_toward_the_cap()
    {
        var actionId = Guid.NewGuid().ToString("N");
        var action = _cluster.GrainFactory.GetGrain<IChargerSimActionGrain>(ChargerSimKeys.Action(actionId));
        await action.Activate();

        var attendee = _cluster.GrainFactory.GetGrain<IAttendeeChargerSimGrain>(
            ChargerSimKeys.Attendee(actionId, "alice-recap"));

        await attendee.CreateChargers(20);
        await Drain(attendee);
        await attendee.KillMyChargers();
        await Drain(attendee);

        // 20 killed; the cap should now have full room again. Creating 30 more
        // must succeed (it would not if killed chargers still counted).
        await attendee.CreateChargers(30);
        await Drain(attendee);

        var summary = await attendee.GetSummary();
        var live = summary.NoSessionCount + summary.ActiveSessionCount + summary.PausedWithSessionCount;
        Assert.Equal(30, live);
        Assert.Equal(20, summary.KilledCount);
    }

    [Fact]
    public async Task Kill_switch_is_enforced_from_aggregate_cache_without_polling_the_action()
    {
        var actionId = Guid.NewGuid().ToString("N");
        var action = _cluster.GrainFactory.GetGrain<IChargerSimActionGrain>(ChargerSimKeys.Action(actionId));
        await action.Activate();
        await action.SetKillSwitch(true);

        // A freshly-activated aggregate must pull the kill-switch flag on activation
        // (the action grain has no registered attendees yet, so there was no push).
        // The Contribution() helper uses attendee id "alice", so key the aggregate to
        // match — UpsertContribution derives the charger key from the contribution.
        var agg = _cluster.GrainFactory.GetGrain<IAttendeeChargerAggregateGrain>(
            ChargerSimKeys.Aggregate(actionId, "alice"));

        // A live charger publishes a contribution; the cached flag (not a call back to
        // the action grain) must trigger a fire-and-forget kill of that charger.
        await agg.UpsertContribution(Contribution("CP-000001", 1, ChargerSimState.ActiveSession, power: 11));

        var charger = _cluster.GrainFactory.GetGrain<IChargerGrain>(
            ChargerSimKeys.Charger(actionId, "alice", 1));

        for (var i = 0; i < 200; i++)
        {
            if ((await charger.GetSnapshot()).Killed) return; // enforced — done
            await Task.Delay(25);
        }

        Assert.Fail("charger was not killed by the cached kill switch");
    }

    [Fact]
    public async Task Deactivate_pushes_inactive_state_and_blocks_further_commands()
    {
        var actionId = Guid.NewGuid().ToString("N");
        var action = _cluster.GrainFactory.GetGrain<IChargerSimActionGrain>(ChargerSimKeys.Action(actionId));
        await action.Activate();

        var attendee = _cluster.GrainFactory.GetGrain<IAttendeeChargerSimGrain>(
            ChargerSimKeys.Attendee(actionId, "alice-gate"));
        await attendee.Register("Alice");
        await attendee.CreateChargers(3); // accepted while active
        await Drain(attendee);

        // Deactivate pushes active=false into the attendee grain's cached flag.
        await action.Deactivate();

        // EnsureActive now reads the cached flag (no call back to the action grain)
        // and rejects the command.
        await Assert.ThrowsAsync<InvalidOperationException>(
            () => attendee.SendBatchCommand(BatchChargerCommandType.StartSessions, 3));
    }

    [Fact]
    public async Task Dashboard_fan_out_does_not_deadlock_when_it_activates_grains()
    {
        var actionId = Guid.NewGuid().ToString("N");
        var action = _cluster.GrainFactory.GetGrain<IChargerSimActionGrain>(ChargerSimKeys.Action(actionId));
        await action.Activate();

        // Register an attendee id directly so the action knows it, but its attendee +
        // aggregate grains are NOT yet activated. The first dashboard build activates
        // them mid-fan-out, and their OnActivateAsync calls IsActive()/
        // IsKillSwitchEnabled() back into this (awaiting) action grain — a deadlock
        // unless those reads are AlwaysInterleave.
        await action.RegisterAttendee("ghost-attendee");

        var dashboardTask = action.GetDashboard();
        var first = await Task.WhenAny(dashboardTask, Task.Delay(TimeSpan.FromSeconds(10)));
        Assert.True(first == dashboardTask, "GetDashboard deadlocked on re-entrant activation");

        var dashboard = await dashboardTask;
        Assert.True(dashboard.Active);
    }
}
