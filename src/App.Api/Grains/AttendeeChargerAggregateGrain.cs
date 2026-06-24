using App.Api.GrainContracts;

namespace App.Api.Grains;

/// <summary>
/// In-memory aggregate state for one attendee: the last known contribution per
/// charger, plus the running fleet summary it maintains incrementally.
/// </summary>
internal sealed class AttendeeChargerAggregateState
{
    /// <summary>Last known absolute contribution per charger id.</summary>
    public Dictionary<string, ChargerAggregateContribution> Contributions { get; } = new();

    public ChargerFleetSummary Summary { get; set; } = new();

    /// <summary>
    /// Cached copy of the action's kill-switch flag. Pushed by the action grain on
    /// toggle (see <see cref="AttendeeChargerAggregateGrain.SetKillSwitch"/>) and
    /// pulled once on activation, so the hot per-contribution path never calls back
    /// into the action grain.
    /// </summary>
    public bool KillSwitchEnabled { get; set; }
}

/// <summary>
/// One aggregate grain per attendee. It receives absolute contributions from
/// each charger and keeps a live <see cref="ChargerFleetSummary"/> without ever
/// reaching back out to the individual charger grains. Updates are idempotent and
/// order-independent thanks to the per-charger version check.
///
/// This state is deliberately NOT persisted: it is a live roll-up that every
/// charger re-publishes on each 30-second tick and after every command, so it is
/// fully reconstructed within one tick if the grain is reactivated. Persisting it
/// would add a storage write on every contribution (potentially tens of thousands
/// per attendee per tick) for data we never need to durably keep.
/// </summary>
public sealed class AttendeeChargerAggregateGrain : Grain, IAttendeeChargerAggregateGrain
{
    private readonly AttendeeChargerAggregateState _state = new();
    private string _actionId = "";

    public override async Task OnActivateAsync(CancellationToken cancellationToken)
    {
        // Key: "action-{actionId}/attendee-{attendeeId}/aggregate".
        var parts = this.GetPrimaryKeyString().Split('/');
        if (parts.Length >= 1)
        {
            _actionId = parts[0]["action-".Length..];
        }
        if (parts.Length >= 2 && parts[1].StartsWith("attendee-", StringComparison.Ordinal))
        {
            _state.Summary.AttendeeId = parts[1]["attendee-".Length..];
        }

        await base.OnActivateAsync(cancellationToken);

        // Pull the current kill-switch flag once, so we start in sync with the action
        // grain even if this aggregate activated after a toggle (or after a silo
        // restart). From here on the flag is kept fresh by the action grain's push.
        // One cold call per activation, vs the thousands of hot per-contribution
        // calls this replaces.
        _state.KillSwitchEnabled = await Action.IsKillSwitchEnabled();
    }

    public Task<ChargerFleetSummary> UpsertContribution(ChargerAggregateContribution contribution)
    {
        var summary = _state.Summary;

        if (_state.Contributions.TryGetValue(contribution.ChargerId, out var previous))
        {
            // Ignore duplicate or out-of-order updates.
            if (contribution.Version <= previous.Version)
            {
                return Task.FromResult(summary);
            }

            Apply(summary, previous, -1);
        }

        // If the kill switch is on and this charger is still alive, kill it now.
        // The flag is read from the local cache (pushed by the action grain), so this
        // hot path never calls back into the single action grain. Fire-and-forget:
        // the charger publishes its own Killed contribution once Kill() completes,
        // which updates the summary on the next call.
        if (contribution.State != ChargerSimState.Killed && _state.KillSwitchEnabled)
        {
            var number = ChargerSimKeys.NumberFromDisplayId(contribution.ChargerId);
            if (number > 0)
            {
                var chargerKey = ChargerSimKeys.Charger(_actionId, contribution.AttendeeId, number);
                _ = GrainFactory.GetGrain<IChargerGrain>(chargerKey).Kill();
            }
        }

        Apply(summary, contribution, +1);
        _state.Contributions[contribution.ChargerId] = contribution;

        summary.TotalChargers = _state.Contributions.Count;
        if (contribution.UpdatedAt > summary.LastUpdatedAt)
        {
            summary.LastUpdatedAt = contribution.UpdatedAt;
        }

        return Task.FromResult(summary);
    }

    public Task<ChargerFleetSummary> GetSummary() => Task.FromResult(_state.Summary);

    public Task SetKillSwitch(bool enabled)
    {
        _state.KillSwitchEnabled = enabled;
        return Task.CompletedTask;
    }

    public Task<IReadOnlyList<ChargerAggregateContribution>> GetRecentContributions(int take)
    {
        IReadOnlyList<ChargerAggregateContribution> recent = _state.Contributions.Values
            .OrderByDescending(c => c.UpdatedAt)
            .Take(Math.Max(0, take))
            .ToList();
        return Task.FromResult(recent);
    }

    public Task<IReadOnlyList<ChargerCellState>> GetStateSample(int take)
    {
        take = Math.Max(0, take);

        // Killed chargers are never deleted from the aggregate — they're upserted to
        // the Killed state so their grid cell stays put — so over a long session the
        // kill-and-recreate churn can grow the contribution count past `take`. When it
        // does, a naive insertion-order sample fills up with stale killed cells and can
        // crowd the current live fleet out of the grid entirely. So once we're over
        // capacity, drop killed chargers first: keep every live/paused/idle charger we
        // can fit and only backfill any leftover slots with killed ones. Within each
        // group we keep insertion order, which is stable across polls (entries are never
        // removed), so the sampled set stays stable as the fleet grows.
        var values = _state.Contributions.Values;
        IEnumerable<ChargerAggregateContribution> selected;
        if (values.Count <= take)
        {
            selected = values;
        }
        else
        {
            var live = values.Where(c => c.State != ChargerSimState.Killed).Take(take).ToList();
            var killed = values.Where(c => c.State == ChargerSimState.Killed).Take(take - live.Count);
            selected = live.Concat(killed);
        }

        // Scatter the chosen cells' grid positions with a deterministic per-charger hash.
        // Why scatter: chargers are created and fire their 30-second reminders in roughly
        // contiguous number order, so insertion order clusters same-aged chargers together.
        // Rendered in that order, a wave of timers firing lights up a solid block of the
        // grid at once. Ordering by a hash of the (immutable) charger id spreads each wave
        // evenly across the whole grid, so transitions shimmer across the fleet instead of
        // marching as blocks — and because the hash depends only on the id, every cell
        // still keeps a fixed position between polls.
        IReadOnlyList<ChargerCellState> sample = selected
            .OrderBy(c => Scatter(c.ChargerId))
            .Select(c => new ChargerCellState(c.State, c.ActivePowerKw, c.MaxPowerKw))
            .ToList();
        return Task.FromResult(sample);
    }

    // Deterministic, well-distributed hash of a charger id, used purely to scatter
    // grid cell order (see GetStateSample). Deliberately not String.GetHashCode, which
    // is salted per process and so would reshuffle the entire grid on every silo
    // restart; this FNV-1a hash gives each id a fixed grid slot for the demo's life.
    private static uint Scatter(string id)
    {
        uint hash = 2166136261u;
        foreach (var ch in id)
        {
            hash ^= ch;
            hash *= 16777619u;
        }
        return hash;
    }

    public Task<IReadOnlyList<string>> SelectChargerIds(ChargerSelectionFilter filter, int take)
    {
        IReadOnlyList<string> ids = _state.Contributions.Values
            .Where(c => Matches(c, filter))
            .Take(Math.Max(0, take))
            .Select(c => c.ChargerId)
            .ToList();
        return Task.FromResult(ids);
    }

    public Task<string?> GetRandomChargerInState(ChargerSimState state)
    {
        var candidates = _state.Contributions.Values
            .Where(c => c.State == state)
            .ToList();

        if (candidates.Count == 0)
        {
            return Task.FromResult<string?>(null);
        }

        return Task.FromResult<string?>(candidates[Random.Shared.Next(candidates.Count)].ChargerId);
    }

    public Task Reset()
    {
        var attendeeId = _state.Summary.AttendeeId;
        _state.Contributions.Clear();
        _state.Summary = new ChargerFleetSummary { AttendeeId = attendeeId };
        return Task.CompletedTask;
    }

    private IChargerSimActionGrain Action =>
        GrainFactory.GetGrain<IChargerSimActionGrain>(ChargerSimKeys.Action(_actionId));

    private static bool Matches(ChargerAggregateContribution c, ChargerSelectionFilter filter) => filter switch
    {
        ChargerSelectionFilter.WithoutSession => c.State == ChargerSimState.NoSession,
        ChargerSelectionFilter.ActiveSessions => c.State == ChargerSimState.ActiveSession,
        ChargerSelectionFilter.PausedSessions => c.State == ChargerSimState.PausedWithSession,
        ChargerSelectionFilter.ActiveOrPausedSessions =>
            c.State is ChargerSimState.ActiveSession or ChargerSimState.PausedWithSession,
        ChargerSelectionFilter.Any => c.State != ChargerSimState.Killed,
        _ => false
    };

    // Adds (sign=+1) or removes (sign=-1) a single contribution's effect from the
    // running summary. TotalChargers is handled by the caller from the dict count.
    private static void Apply(ChargerFleetSummary s, ChargerAggregateContribution c, int sign)
    {
        switch (c.State)
        {
            case ChargerSimState.NoSession: s.NoSessionCount += sign; break;
            case ChargerSimState.ActiveSession: s.ActiveSessionCount += sign; break;
            case ChargerSimState.PausedWithSession: s.PausedWithSessionCount += sign; break;
            case ChargerSimState.Killed: s.KilledCount += sign; break;
        }

        if (c.HasSession)
        {
            s.ChargersWithSessionCount += sign;
        }

        s.TotalActivePowerKw += sign * c.ActivePowerKw;
        s.TotalSessionKwh += sign * c.SessionKwh;

        // Guard against tiny floating-point drift accumulating into negatives.
        if (s.TotalActivePowerKw < 0) s.TotalActivePowerKw = 0;
        if (s.TotalSessionKwh < 0) s.TotalSessionKwh = 0;
    }
}
