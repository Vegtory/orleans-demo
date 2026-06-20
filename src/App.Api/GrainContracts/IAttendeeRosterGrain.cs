namespace App.Api.GrainContracts;

/// <summary>
/// One attendee's presence on the roster: their display name and when they were
/// last seen calling the presentation.
/// </summary>
[GenerateSerializer]
public sealed record AttendeePresence(
    [property: Id(0)] string Name,
    [property: Id(1)] DateTimeOffset LastSeen);

/// <summary>
/// The presenter's view of who is currently attending: a count plus the list of
/// attendees seen within the active window, most-recent first.
/// </summary>
[GenerateSerializer]
public sealed record AttendeeRosterView(
    [property: Id(0)] int Count,
    [property: Id(1)] AttendeePresence[] Attendees);

/// <summary>
/// The single global attendee roster. Records a heartbeat each time an attendee
/// polls the presentation and reports those seen within <see cref="ActiveWindow"/>.
/// Presence is transient by nature, so this grain holds its state in memory only.
/// </summary>
public interface IAttendeeRosterGrain : IGrainWithStringKey
{
    /// <summary>The well-known key of the one global roster grain.</summary>
    public const string GlobalKey = "global";

    /// <summary>
    /// An attendee counts as present only if they have called the presentation
    /// within this window.
    /// </summary>
    public static readonly TimeSpan ActiveWindow = TimeSpan.FromMinutes(10);

    /// <summary>Records that the given attendee just called the presentation.</summary>
    Task Heartbeat(string attendeeKey, string name);

    /// <summary>Returns the attendees seen within <see cref="ActiveWindow"/>.</summary>
    Task<AttendeeRosterView> GetActive();
}
