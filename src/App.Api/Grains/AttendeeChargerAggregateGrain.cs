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

    public override Task OnActivateAsync(CancellationToken cancellationToken)
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

        return base.OnActivateAsync(cancellationToken);
    }

    public async Task<ChargerFleetSummary> UpsertContribution(ChargerAggregateContribution contribution)
    {
        var summary = _state.Summary;

        if (_state.Contributions.TryGetValue(contribution.ChargerId, out var previous))
        {
            // Ignore duplicate or out-of-order updates.
            if (contribution.Version <= previous.Version)
            {
                return summary;
            }

            Apply(summary, previous, -1);
        }

        // If the kill switch is on and this charger is still alive, kill it now.
        // Fire-and-forget: the charger will publish its own Killed contribution
        // once Kill() completes, which will update the summary on the next call.
        if (contribution.State != ChargerSimState.Killed && await Action.IsKillSwitchEnabled())
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

        return summary;
    }

    public Task<ChargerFleetSummary> GetSummary() => Task.FromResult(_state.Summary);

    public Task<IReadOnlyList<ChargerAggregateContribution>> GetRecentContributions(int take)
    {
        IReadOnlyList<ChargerAggregateContribution> recent = _state.Contributions.Values
            .OrderByDescending(c => c.UpdatedAt)
            .Take(Math.Max(0, take))
            .ToList();
        return Task.FromResult(recent);
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
