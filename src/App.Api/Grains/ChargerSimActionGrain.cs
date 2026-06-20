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

    private readonly IPersistentState<ChargerSimActionState> _state;

    // The recent-events ticker is live, throwaway UI data, so it is kept in
    // memory rather than persisted. This is also what makes RecordEvent safe to
    // mark [AlwaysInterleave]: with no WriteStateAsync there is no await, so
    // concurrent RecordEvent calls (e.g. several attendees during KillAllChargers)
    // run to completion in a single turn and cannot race on the storage etag.
    private readonly LinkedList<string> _recentEvents = new();
    private string _actionId = "";

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
    }

    public async Task Deactivate()
    {
        _state.State.Active = false;
        await _state.WriteStateAsync();
    }

    public Task<bool> IsActive() => Task.FromResult(_state.State.Active);

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
        var summaries = await GetAllAttendeeSummaries();
        var global = Combine(summaries);
        global.AttendeeName = "All attendees";

        // Newest first.
        var events = _recentEvents.ToArray();
        return new ChargerSimDashboard(_state.State.Active, global, summaries.ToArray(), events, _state.State.KillSwitchEnabled);
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
        if (enabled)
        {
            await KillAllChargers();
            await RecordEvent("Kill switch engaged");
        }
        else
        {
            await RecordEvent("Kill switch disengaged");
        }
    }

    public async Task Delete()
    {
        await KillAllChargers();
        await _state.ClearStateAsync();
        DeactivateOnIdle();
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
