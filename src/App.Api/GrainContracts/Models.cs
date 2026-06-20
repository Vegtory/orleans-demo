namespace App.Api.GrainContracts;

// Shared DTOs returned across grain boundaries. They are serialized by Orleans,
// so each carries [GenerateSerializer] + [Id(n)] like the grain state classes.

/// <summary>The kind of presentation action a presenter has created.</summary>
public enum ActionKind
{
    MultipleChoice,
    ChargerSim
}

/// <summary>A presenter's view of one action it created.</summary>
[GenerateSerializer]
public sealed record ActionSummary(
    [property: Id(0)] string Id,
    [property: Id(1)] string Title,
    [property: Id(2)] int OptionCount,
    [property: Id(3)] ActionKind Kind = ActionKind.MultipleChoice);

/// <summary>The renderable form of a multiple-choice action.</summary>
[GenerateSerializer]
public sealed record QuestionView(
    [property: Id(0)] string ActionId,
    [property: Id(1)] string Title,
    [property: Id(2)] string[] Options);

/// <summary>Tallied answers for a multiple-choice action.</summary>
[GenerateSerializer]
public sealed record ResultsView(
    [property: Id(0)] string ActionId,
    [property: Id(1)] string Title,
    [property: Id(2)] string[] Options,
    [property: Id(3)] int[] Counts,
    [property: Id(4)] int Total);

/// <summary>What a presenter polls: name, created actions, and which is active.</summary>
[GenerateSerializer]
public sealed record PresenterView(
    [property: Id(0)] string Name,
    [property: Id(1)] ActionSummary[] Actions,
    [property: Id(2)] string? ActiveActionId);

/// <summary>
/// What an attendee polls: name, the focused multiple-choice question (if any)
/// and their answer. When a ChargerSim action is live instead,
/// <see cref="ChargerSimActionId"/> is set and the multiple-choice focus is null.
/// </summary>
[GenerateSerializer]
public sealed record AttendeeView(
    [property: Id(0)] string Name,
    [property: Id(1)] QuestionView? Focus,
    [property: Id(2)] int? YourAnswer,
    [property: Id(3)] string? ChargerSimActionId = null);
