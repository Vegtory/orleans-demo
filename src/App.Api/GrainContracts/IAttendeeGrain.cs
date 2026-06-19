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
}
