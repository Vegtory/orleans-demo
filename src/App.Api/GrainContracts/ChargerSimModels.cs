namespace App.Api.GrainContracts;

// ---------------------------------------------------------------------------
// ChargerSim: a demo presentation action where every attendee runs their own
// fleet of simulated EV chargers. Each charger is its own Orleans grain with
// its own 30-second reminder; each attendee has one aggregate grain that keeps
// a live fleet summary without ever polling the individual chargers.
//
// These DTOs/enums cross grain boundaries, so each serializable type carries
// [GenerateSerializer] + [Id(n)] like the rest of the contracts.
// ---------------------------------------------------------------------------

/// <summary>The lifecycle state of a single simulated charger.</summary>
public enum ChargerSimState
{
    NoSession,
    ActiveSession,
    PausedWithSession,
    Killed
}

/// <summary>A batch command an attendee can fan out across many of their chargers.</summary>
public enum BatchChargerCommandType
{
    StartSessions,
    StopCharging,
    StopSessions,
    LowerPowerUsage,
    RandomChaos,
    Kill,
    IncreasePowerUsage
}

/// <summary>A command an attendee can issue against a single opened charger.</summary>
public enum SingleChargerCommandType
{
    StartSession,
    PauseCharging,
    ResumeCharging,
    StopSession,
    LowerPower,
    Kill,
    IncreasePower
}

/// <summary>Which chargers a batch command should target, resolved from the aggregate snapshot.</summary>
public enum ChargerSelectionFilter
{
    WithoutSession,
    ActiveSessions,
    ActiveOrPausedSessions,
    Any
}

/// <summary>
/// The absolute current contribution a charger publishes to its attendee's
/// aggregate grain after every simulation tick and every command. Absolute (not
/// a delta) + monotonically increasing <see cref="Version"/> makes aggregation
/// idempotent and safe against duplicate or out-of-order updates.
/// </summary>
[GenerateSerializer]
public sealed record ChargerAggregateContribution
{
    [Id(0)] public string AttendeeId { get; init; } = "";
    [Id(1)] public string ChargerId { get; init; } = "";
    [Id(2)] public long Version { get; init; }
    [Id(3)] public ChargerSimState State { get; init; }
    [Id(4)] public bool HasSession { get; init; }
    [Id(5)] public double ActivePowerKw { get; init; }
    [Id(6)] public double SessionKwh { get; init; }
    [Id(7)] public DateTimeOffset UpdatedAt { get; init; }
}

/// <summary>A live, roll-up view of one attendee's charger fleet, maintained incrementally.</summary>
[GenerateSerializer]
public sealed class ChargerFleetSummary
{
    [Id(0)] public string AttendeeId { get; set; } = "";
    [Id(1)] public int TotalChargers { get; set; }
    [Id(2)] public int NoSessionCount { get; set; }
    [Id(3)] public int ActiveSessionCount { get; set; }
    [Id(4)] public int PausedWithSessionCount { get; set; }
    [Id(5)] public int KilledCount { get; set; }
    [Id(6)] public int ChargersWithSessionCount { get; set; }
    [Id(7)] public double TotalActivePowerKw { get; set; }
    [Id(8)] public double TotalSessionKwh { get; set; }
    [Id(9)] public DateTimeOffset LastUpdatedAt { get; set; }

    /// <summary>Optional human label, set when the summary is returned for a specific attendee.</summary>
    [Id(10)] public string AttendeeName { get; set; } = "";
}

/// <summary>
/// The attendee's outstanding background work: chargers still to be created and
/// batch commands still queued. The attendee's controller grain accepts create
/// and command requests instantly and drains this work on a background timer, so
/// the UI polls this to show "still working" without blocking on the request.
/// </summary>
[GenerateSerializer]
public sealed record ChargerSimWorkStatus(
    [property: Id(0)] int PendingChargers,
    [property: Id(1)] int QueuedCommands);

/// <summary>A point-in-time snapshot of one charger, returned only when an attendee opens it.</summary>
[GenerateSerializer]
public sealed record ChargerSnapshot(
    [property: Id(0)] string ChargerId,
    [property: Id(1)] string AttendeeId,
    [property: Id(2)] ChargerSimState State,
    [property: Id(3)] string? ActiveSessionId,
    [property: Id(4)] double ActivePowerKw,
    [property: Id(5)] double MaxPowerKw,
    [property: Id(6)] double SessionKwh,
    [property: Id(7)] DateTimeOffset? SessionStartedAt,
    [property: Id(8)] DateTimeOffset LastUpdatedAt,
    [property: Id(9)] bool Killed,
    [property: Id(10)] long Version);

/// <summary>Everything the presenter dashboard needs in a single grain call.</summary>
[GenerateSerializer]
public sealed record ChargerSimDashboard(
    [property: Id(0)] bool Active,
    [property: Id(1)] ChargerFleetSummary Global,
    [property: Id(2)] ChargerFleetSummary[] Attendees,
    [property: Id(3)] string[] RecentEvents,
    [property: Id(4)] bool KillSwitchEnabled = false);

/// <summary>
/// Stable, readable grain-key conventions for the ChargerSim grain graph, plus
/// the short display id ("CP-000042") shown in the UI. The display id encodes
/// the charger number so it can be mapped back to a grain key without lookups.
/// </summary>
public static class ChargerSimKeys
{
    public static string Action(string actionId) => $"action-{actionId}";

    public static string Attendee(string actionId, string attendeeId) =>
        $"action-{actionId}/attendee-{attendeeId}";

    public static string Aggregate(string actionId, string attendeeId) =>
        $"{Attendee(actionId, attendeeId)}/aggregate";

    public static string Charger(string actionId, string attendeeId, int number) =>
        $"{Attendee(actionId, attendeeId)}/charger-{number}";

    public static string DisplayId(int number) => $"CP-{number:000000}";

    /// <summary>Recovers the charger number from a "CP-000042" display id, or -1 if malformed.</summary>
    public static int NumberFromDisplayId(string displayId)
    {
        if (displayId is { Length: > 3 } && displayId.StartsWith("CP-", StringComparison.Ordinal)
            && int.TryParse(displayId.AsSpan(3), out var n))
        {
            return n;
        }

        return -1;
    }

    /// <summary>Parses a charger grain key into its parts. Throws if the key is not a charger key.</summary>
    public static (string ActionId, string AttendeeId, int Number) ParseChargerKey(string key)
    {
        var parts = key.Split('/');
        if (parts.Length != 3
            || !parts[0].StartsWith("action-", StringComparison.Ordinal)
            || !parts[1].StartsWith("attendee-", StringComparison.Ordinal)
            || !parts[2].StartsWith("charger-", StringComparison.Ordinal)
            || !int.TryParse(parts[2].AsSpan("charger-".Length), out var number))
        {
            throw new ArgumentException($"Not a valid charger grain key: '{key}'.", nameof(key));
        }

        return (parts[0]["action-".Length..], parts[1]["attendee-".Length..], number);
    }
}
