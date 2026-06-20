namespace App.Api.GrainContracts;

/// <summary>
/// A multiple-choice question action. The grain key is the action id.
/// Attendees answer by index; each attendee has at most one (changeable) answer.
/// </summary>
public interface IMultipleChoiceGrain : IGrainWithStringKey
{
    Task Configure(string title, string[] options);
    Task<QuestionView> GetQuestion();

    /// <summary>Records or replaces an attendee's choice. Throws if the index is out of range.</summary>
    Task Answer(string attendeeKey, int optionIndex);

    Task<int?> GetAnswer(string attendeeKey);
    Task<ResultsView> GetResults();

    /// <summary>Clears all persisted state for this action.</summary>
    Task Delete();
}
