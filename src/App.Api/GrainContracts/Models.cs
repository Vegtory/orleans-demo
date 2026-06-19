namespace App.Api.GrainContracts;

// Shared DTOs returned across grain boundaries. They are serialized by Orleans,
// so each carries [GenerateSerializer] + [Id(n)] like the grain state classes.

/// <summary>A presenter's view of one action it created.</summary>
[GenerateSerializer]
public sealed record ActionSummary(
    [property: Id(0)] string Id,
    [property: Id(1)] string Title,
    [property: Id(2)] int OptionCount);

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

/// <summary>What an attendee polls: name, the focused question (if any) and their answer.</summary>
[GenerateSerializer]
public sealed record AttendeeView(
    [property: Id(0)] string Name,
    [property: Id(1)] QuestionView? Focus,
    [property: Id(2)] int? YourAnswer);
