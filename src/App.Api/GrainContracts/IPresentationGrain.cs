namespace App.Api.GrainContracts;

/// <summary>The action currently in focus on the global presentation.</summary>
[GenerateSerializer]
public sealed record PresentationFocus(
    [property: Id(0)] string ActionId,
    [property: Id(1)] ActionKind Kind);

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

    /// <summary>
    /// The action in focus and its kind, or null when nothing is live. Returns a
    /// nullable reference type rather than a nullable enum so it serializes across
    /// silos (Orleans does not allow <c>Nullable&lt;enum&gt;</c> as a value).
    /// </summary>
    Task<PresentationFocus?> GetFocusInfo();
}
