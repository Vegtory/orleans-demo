using App.Api.GrainContracts;

namespace App.Api.Grains;

/// <summary>
/// In-memory roster of attendees who have recently called the presentation.
/// Each <see cref="Heartbeat"/> stamps the attendee's last-seen time;
/// <see cref="GetActive"/> prunes anyone outside the active window and returns
/// the rest. State is intentionally volatile — presence does not need to survive
/// a silo restart, and a single activation serves the whole cluster.
/// </summary>
public sealed class AttendeeRosterGrain : Grain, IAttendeeRosterGrain
{
    // Keyed by the attendee's grain key so repeat polls update rather than
    // duplicate. Value is the attendee's display name and last-seen timestamp.
    private readonly Dictionary<string, (string Name, DateTimeOffset LastSeen)> _seen = new();

    public Task Heartbeat(string attendeeKey, string name)
    {
        _seen[attendeeKey] = (name, DateTimeOffset.UtcNow);
        return Task.CompletedTask;
    }

    public Task<AttendeeRosterView> GetActive()
    {
        var cutoff = DateTimeOffset.UtcNow - IAttendeeRosterGrain.ActiveWindow;

        // Drop stale entries so the roster (and this grain's memory) stays bounded.
        foreach (var key in _seen.Where(kv => kv.Value.LastSeen < cutoff).Select(kv => kv.Key).ToList())
        {
            _seen.Remove(key);
        }

        var attendees = _seen.Values
            .OrderByDescending(v => v.LastSeen)
            .ThenBy(v => v.Name, StringComparer.OrdinalIgnoreCase)
            .Select(v => new AttendeePresence(v.Name, v.LastSeen))
            .ToArray();

        return Task.FromResult(new AttendeeRosterView(attendees.Length, attendees));
    }
}
