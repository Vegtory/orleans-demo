using App.Api.GrainContracts;

namespace App.Api.Grains;

/// <summary>
/// Persisted aggregate state for one attendee: the last known contribution per
/// charger, plus the running fleet summary it maintains incrementally.
/// </summary>
[GenerateSerializer]
public sealed class AttendeeChargerAggregateState
{
    /// <summary>Last known absolute contribution per charger id.</summary>
    [Id(0)]
    public Dictionary<string, ChargerAggregateContribution> Contributions { get; set; } = new();

    [Id(1)]
    public ChargerFleetSummary Summary { get; set; } = new();
}

/// <summary>
/// One aggregate grain per attendee. It receives absolute contributions from
/// each charger and keeps a live <see cref="ChargerFleetSummary"/> without ever
/// reaching back out to the individual charger grains. Updates are idempotent and
/// order-independent thanks to the per-charger version check.
/// </summary>
public sealed class AttendeeChargerAggregateGrain : Grain, IAttendeeChargerAggregateGrain
{
    private readonly IPersistentState<AttendeeChargerAggregateState> _state;

    public AttendeeChargerAggregateGrain(
        [PersistentState("chargerAggregate", "store")] IPersistentState<AttendeeChargerAggregateState> state)
    {
        _state = state;
    }

    public override Task OnActivateAsync(CancellationToken cancellationToken)
    {
        // The attendee id is the middle segment of the grain key
        // "action-{actionId}/attendee-{attendeeId}/aggregate".
        if (string.IsNullOrEmpty(_state.State.Summary.AttendeeId))
        {
            var parts = this.GetPrimaryKeyString().Split('/');
            if (parts.Length >= 2 && parts[1].StartsWith("attendee-", StringComparison.Ordinal))
            {
                _state.State.Summary.AttendeeId = parts[1]["attendee-".Length..];
            }
        }

        return base.OnActivateAsync(cancellationToken);
    }

    public async Task<ChargerFleetSummary> UpsertContribution(ChargerAggregateContribution contribution)
    {
        var summary = _state.State.Summary;

        if (_state.State.Contributions.TryGetValue(contribution.ChargerId, out var previous))
        {
            // Ignore duplicate or out-of-order updates.
            if (contribution.Version <= previous.Version)
            {
                return summary;
            }

            Apply(summary, previous, -1);
        }

        Apply(summary, contribution, +1);
        _state.State.Contributions[contribution.ChargerId] = contribution;

        summary.TotalChargers = _state.State.Contributions.Count;
        if (contribution.UpdatedAt > summary.LastUpdatedAt)
        {
            summary.LastUpdatedAt = contribution.UpdatedAt;
        }

        await _state.WriteStateAsync();
        return summary;
    }

    public Task<ChargerFleetSummary> GetSummary() => Task.FromResult(_state.State.Summary);

    public Task<IReadOnlyList<ChargerAggregateContribution>> GetRecentContributions(int take)
    {
        IReadOnlyList<ChargerAggregateContribution> recent = _state.State.Contributions.Values
            .OrderByDescending(c => c.UpdatedAt)
            .Take(Math.Max(0, take))
            .ToList();
        return Task.FromResult(recent);
    }

    public Task<IReadOnlyList<string>> SelectChargerIds(ChargerSelectionFilter filter, int take)
    {
        IReadOnlyList<string> ids = _state.State.Contributions.Values
            .Where(c => Matches(c, filter))
            .Take(Math.Max(0, take))
            .Select(c => c.ChargerId)
            .ToList();
        return Task.FromResult(ids);
    }

    public Task<string?> GetRandomChargerInState(ChargerSimState state)
    {
        var candidates = _state.State.Contributions.Values
            .Where(c => c.State == state)
            .ToList();

        if (candidates.Count == 0)
        {
            return Task.FromResult<string?>(null);
        }

        return Task.FromResult<string?>(candidates[Random.Shared.Next(candidates.Count)].ChargerId);
    }

    public async Task Reset()
    {
        var attendeeId = _state.State.Summary.AttendeeId;
        _state.State.Contributions.Clear();
        _state.State.Summary = new ChargerFleetSummary { AttendeeId = attendeeId };
        await _state.WriteStateAsync();
    }

    private static bool Matches(ChargerAggregateContribution c, ChargerSelectionFilter filter) => filter switch
    {
        ChargerSelectionFilter.WithoutSession => c.State == ChargerSimState.NoSession,
        ChargerSelectionFilter.ActiveSessions => c.State == ChargerSimState.ActiveSession,
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
