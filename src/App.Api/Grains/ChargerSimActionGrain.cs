using App.Api.GrainContracts;
using Orleans.Runtime;

namespace App.Api.Grains;

/// <summary>Persisted state for a ChargerSim action.</summary>
[GenerateSerializer]
public sealed class ChargerSimActionState
{
    [Id(0)] public bool Active { get; set; }

    /// <summary>Attendee ids (their attendee grain keys) that have joined this action.</summary>
    [Id(1)] public HashSet<string> Attendees { get; set; } = new();

    [Id(2)] public bool KillSwitchEnabled { get; set; }

    /// <summary>Room-wide collaborative target total active power (kW). 0 means no goal set.</summary>
    [Id(3)] public double GoalActivePowerKw { get; set; }
}

/// <summary>
/// The top-level ChargerSim action. It gates attendee controls (active/inactive),
/// tracks which attendees have joined, and rolls their per-attendee aggregate
/// summaries up into a global view for the presenter — without ever touching the
/// individual charger grains.
/// </summary>
public sealed class ChargerSimActionGrain : Grain, IChargerSimActionGrain
{
    private const int MaxEvents = 25;

    // The presenter dashboard is served from a cached snapshot refreshed on this
    // interval, so each presenter poll is a single grain call instead of a 2N+1
    // fan-out to every attendee + aggregate grain. If no presenter has polled within
    // the idle timeout, the refresh timer stops so this grain can deactivate.
    private static readonly TimeSpan DashboardRefreshInterval = TimeSpan.FromSeconds(1);
    private static readonly TimeSpan DashboardIdleTimeout = TimeSpan.FromSeconds(15);

    private readonly IPersistentState<ChargerSimActionState> _state;

    // The recent-events ticker is live, throwaway UI data, so it is kept in
    // memory rather than persisted. This is also what makes RecordEvent safe to
    // mark [AlwaysInterleave]: with no WriteStateAsync there is no await, so
    // concurrent RecordEvent calls (e.g. several attendees during KillAllChargers)
    // run to completion in a single turn and cannot race on the storage etag.
    private readonly LinkedList<string> _recentEvents = new();
    private string _actionId = "";

    // Cached dashboard fan-out result (summaries only — the live event ticker is
    // overlaid at read time). Refreshed by _dashboardTimer; null until first built.
    private IGrainTimer? _dashboardTimer;
    private ChargerSimDashboard? _cachedDashboard;
    private DateTimeOffset _lastDashboardRequest;

    public ChargerSimActionGrain(
        [PersistentState("chargerSimAction", "store")] IPersistentState<ChargerSimActionState> state)
    {
        _state = state;
    }

    public override Task OnActivateAsync(CancellationToken cancellationToken)
    {
        // Key: "action-{actionId}".
        var key = this.GetPrimaryKeyString();
        _actionId = key.StartsWith("action-", StringComparison.Ordinal) ? key["action-".Length..] : key;
        return base.OnActivateAsync(cancellationToken);
    }

    public async Task Activate()
    {
        _state.State.Active = true;
        await _state.WriteStateAsync();
        await BroadcastActive(true);
    }

    public async Task Deactivate()
    {
        _state.State.Active = false;
        await _state.WriteStateAsync();
        await BroadcastActive(false);
    }

    public Task<bool> IsActive() => Task.FromResult(_state.State.Active);
    public Task<bool> IsKillSwitchEnabled() => Task.FromResult(_state.State.KillSwitchEnabled);

    public async Task RegisterAttendee(string attendeeId)
    {
        if (_state.State.Attendees.Add(attendeeId))
        {
            await _state.WriteStateAsync();
        }
    }

    public async Task<IReadOnlyList<ChargerFleetSummary>> GetAllAttendeeSummaries()
    {
        var tasks = _state.State.Attendees
            .Select(id => GrainFactory
                .GetGrain<IAttendeeChargerSimGrain>(ChargerSimKeys.Attendee(_actionId, id))
                .GetSummary())
            .ToList();

        var summaries = await Task.WhenAll(tasks);
        return summaries
            .OrderByDescending(s => s.TotalChargers)
            .ToList();
    }

    public async Task<ChargerFleetSummary> GetGlobalSummary()
    {
        var summaries = await GetAllAttendeeSummaries();
        return Combine(summaries);
    }

    public async Task<ChargerSimDashboard> GetDashboard()
    {
        _lastDashboardRequest = DateTimeOffset.UtcNow;
        EnsureDashboardTimer();

        // Serve the cached snapshot. Build it inline on the very first call so the
        // first paint (e.g. a presenter reload) shows real data instead of empty;
        // every subsequent poll is served from cache with no fan-out.
        if (_cachedDashboard is null)
        {
            await RefreshDashboard();
        }

        // Overlay the live event ticker so it never lags the (up to 1s stale)
        // summaries. Cheap: an array copy, no grain calls.
        return _cachedDashboard! with { RecentEvents = _recentEvents.ToArray() };
    }

    public async Task<IReadOnlyList<ChargerFleetSummary>> GetLeaderboard()
    {
        // Reuse the dashboard cache: keep the refresh timer alive and build the
        // snapshot inline on the first call, exactly like GetDashboard. Attendees
        // poll this directly, so leaning on the cache keeps the fan-out at ≤1/sec.
        _lastDashboardRequest = DateTimeOffset.UtcNow;
        EnsureDashboardTimer();
        if (_cachedDashboard is null)
        {
            await RefreshDashboard();
        }

        return _cachedDashboard!.Attendees;
    }

    public async Task SetGoal(double targetActivePowerKw)
    {
        var goal = Math.Max(0, double.IsNaN(targetActivePowerKw) ? 0 : targetActivePowerKw);
        if (_state.State.GoalActivePowerKw == goal) return;

        _state.State.GoalActivePowerKw = goal;
        await _state.WriteStateAsync();

        // Reflect the new goal in the cache immediately so the next poll doesn't lag.
        if (_cachedDashboard is not null)
        {
            _cachedDashboard = _cachedDashboard with { GoalActivePowerKw = goal };
        }

        await RecordEvent(goal > 0
            ? $"Presenter set a fleet goal of {goal:N0} kW"
            : "Presenter cleared the fleet goal");
    }

    public async Task<ChargerSimGoalStatus> GetGoalStatus()
    {
        // Reuse the dashboard cache exactly like GetLeaderboard, so attendees polling
        // the shared progress bar never trigger more than one fan-out per second.
        _lastDashboardRequest = DateTimeOffset.UtcNow;
        EnsureDashboardTimer();
        if (_cachedDashboard is null)
        {
            await RefreshDashboard();
        }

        return new ChargerSimGoalStatus(
            _state.State.GoalActivePowerKw,
            _cachedDashboard!.Global.TotalActivePowerKw);
    }

    // Recomputes the cached dashboard by fanning out to the attendee aggregates.
    // Runs on _dashboardTimer (and inline on the first GetDashboard call). The only
    // shared state it writes is the _cachedDashboard reference, assigned after its
    // awaits — it never touches persisted state — so it is safe to interleave.
    private async Task RefreshDashboard()
    {
        // Stop refreshing (and let the grain deactivate) once no presenter has polled
        // for a while. The next GetDashboard re-arms the timer.
        if (_cachedDashboard is not null
            && DateTimeOffset.UtcNow - _lastDashboardRequest > DashboardIdleTimeout)
        {
            StopDashboardTimer();
            return;
        }

        var summaries = await GetAllAttendeeSummaries();
        var global = Combine(summaries);
        global.AttendeeName = "All attendees";

        _cachedDashboard = new ChargerSimDashboard(
            _state.State.Active,
            global,
            summaries.ToArray(),
            _recentEvents.ToArray(),
            _state.State.KillSwitchEnabled,
            _state.State.GoalActivePowerKw);
    }

    private void EnsureDashboardTimer()
    {
        _dashboardTimer ??= this.RegisterGrainTimer(
            callback: static (self, _) => self.RefreshDashboard(),
            state: this,
            options: new GrainTimerCreationOptions
            {
                DueTime = DashboardRefreshInterval,
                Period = DashboardRefreshInterval,
                // Read-only fan-out; interleave so it doesn't serialize behind other
                // turns on this hot grain. KeepAlive is off so a UI cache never pins
                // the grain in memory — the idle timeout lets it deactivate.
                Interleave = true,
                KeepAlive = false
            });
    }

    private void StopDashboardTimer()
    {
        _dashboardTimer?.Dispose();
        _dashboardTimer = null;
    }

    public async Task KillAllChargers()
    {
        // Search the whole cluster for every live charger grain belonging to this
        // action and kill it directly, rather than trusting a registration list
        // (which can miss fleets created without registering, or whose controller
        // grain has since deactivated). Killed chargers deactivate, so they fall
        // out of these statistics — repeated presses only ever target chargers
        // that are still alive.
        var management = GrainFactory.GetGrain<IManagementGrain>(0);
        var stats = await management.GetDetailedGrainStatistics();

        var prefix = ChargerSimKeys.Action(_actionId) + "/";
        var chargerKeys = stats
            .Select(s => s.GrainId.Key.ToString() ?? string.Empty)
            .Where(key => key.StartsWith(prefix, StringComparison.Ordinal)
                && key.Contains("/charger-", StringComparison.Ordinal))
            .Distinct(StringComparer.Ordinal)
            .ToList();

        const int chunk = 500;
        for (var i = 0; i < chargerKeys.Count; i += chunk)
        {
            var tasks = chargerKeys
                .Skip(i)
                .Take(chunk)
                .Select(key => GrainFactory.GetGrain<IChargerGrain>(key).Kill());
            await Task.WhenAll(tasks);
        }

        await RecordEvent($"Presenter killed all chargers ({chargerKeys.Count:N0})");
    }

    public async Task SetKillSwitch(bool enabled)
    {
        _state.State.KillSwitchEnabled = enabled;
        await _state.WriteStateAsync();

        // Push the new flag to every registered aggregate BEFORE the sweep, so the
        // hot per-contribution path reads a local cache instead of calling back into
        // this grain, and so contributions arriving during the sweep already see the
        // new value. Bounded by attendee count (tens), not charger count.
        await BroadcastToAggregates(a => a.SetKillSwitch(enabled));

        if (enabled)
        {
            await RecordEvent("Kill switch engaged");
            // Fire-and-forget: the state is already persisted and pushed, so the
            // aggregate grains will enforce the switch on every charger tick. The
            // sweep below catches grains that are currently alive without waiting.
            _ = KillAllChargers();
        }
        else
        {
            await RecordEvent("Kill switch disengaged");
        }
    }

    public async Task Delete()
    {
        StopDashboardTimer();
        await KillAllChargers();
        await _state.ClearStateAsync();
        DeactivateOnIdle();
    }

    public override Task OnDeactivateAsync(DeactivationReason reason, CancellationToken cancellationToken)
    {
        StopDashboardTimer();
        return base.OnDeactivateAsync(reason, cancellationToken);
    }

    public Task RecordEvent(string message)
    {
        // In-memory only and synchronous (no await): see _recentEvents.
        _recentEvents.AddFirst(message);
        while (_recentEvents.Count > MaxEvents)
        {
            _recentEvents.RemoveLast();
        }

        return Task.CompletedTask;
    }

    // Pushes the active flag to every registered attendee controller grain so their
    // EnsureActive() gate reads a local cache instead of calling back into this grain
    // on every command. Cold path: once per presenter activate/deactivate.
    private Task BroadcastActive(bool active)
    {
        var pushes = _state.State.Attendees
            .Select(id => GrainFactory
                .GetGrain<IAttendeeChargerSimGrain>(ChargerSimKeys.Attendee(_actionId, id))
                .SetActive(active));
        return Task.WhenAll(pushes);
    }

    // Fans a call out to every registered attendee's aggregate grain. Used to push
    // the kill-switch flag down so the hot contribution path never pulls it back up.
    private Task BroadcastToAggregates(Func<IAttendeeChargerAggregateGrain, Task> call)
    {
        var pushes = _state.State.Attendees
            .Select(id => call(GrainFactory
                .GetGrain<IAttendeeChargerAggregateGrain>(ChargerSimKeys.Aggregate(_actionId, id))));
        return Task.WhenAll(pushes);
    }

    // Rolls per-attendee summaries into a single global summary. This reads N
    // attendee aggregate grains (one per attendee), never the chargers themselves.
    private static ChargerFleetSummary Combine(IReadOnlyList<ChargerFleetSummary> summaries)
    {
        var global = new ChargerFleetSummary { AttendeeId = "*" };
        foreach (var s in summaries)
        {
            global.TotalChargers += s.TotalChargers;
            global.NoSessionCount += s.NoSessionCount;
            global.ActiveSessionCount += s.ActiveSessionCount;
            global.PausedWithSessionCount += s.PausedWithSessionCount;
            global.KilledCount += s.KilledCount;
            global.ChargersWithSessionCount += s.ChargersWithSessionCount;
            global.TotalActivePowerKw += s.TotalActivePowerKw;
            global.TotalSessionKwh += s.TotalSessionKwh;
            if (s.LastUpdatedAt > global.LastUpdatedAt)
            {
                global.LastUpdatedAt = s.LastUpdatedAt;
            }
        }

        return global;
    }
}
