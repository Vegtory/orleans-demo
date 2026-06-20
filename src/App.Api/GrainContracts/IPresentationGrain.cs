namespace App.Api.GrainContracts;

/// <summary>
/// The single global presentation. Keyed by the constant <see cref="GlobalKey"/>;
/// it only remembers which action is currently in focus for attendees.
/// </summary>
public interface IPresentationGrain : IGrainWithStringKey
{
    /// <summary>The well-known key of the one global presentation grain.</summary>
    public const string GlobalKey = "global";

    Task SetFocus(string actionId, ActionKind kind = ActionKind.MultipleChoice);
    Task ClearFocus();
    Task<string?> GetFocus();

    /// <summary>The kind of the action currently in focus, or null when nothing is live.</summary>
    Task<ActionKind?> GetFocusKind();
}
