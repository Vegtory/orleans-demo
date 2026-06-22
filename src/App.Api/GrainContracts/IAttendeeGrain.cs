namespace App.Api.GrainContracts;

/// <summary>
/// An attendee. The grain key is "{name}-{6hex}". Attendees can only act on the
/// action the presenter has currently put in focus on the global presentation.
/// </summary>
public interface IAttendeeGrain : IGrainWithStringKey
{
    Task Initialize(string name);
    Task<AttendeeView> GetState();

    /// <summary>
    /// Submits/changes this attendee's answer to the focused action.
    /// Returns false if nothing is currently in focus.
    /// </summary>
    Task<bool> Answer(int optionIndex);

    /// <summary>
    /// Sends an emoji reaction on behalf of this attendee by forwarding it to the
    /// global reaction feed. Routing it through the attendee grain (rather than
    /// pushing straight from the HTTP endpoint) makes the emote a real
    /// grain-to-grain hop, so it shows up in the cluster call visualization.
    /// Unknown kinds are ignored by the feed grain.
    /// </summary>
    Task React(string kind);
}
