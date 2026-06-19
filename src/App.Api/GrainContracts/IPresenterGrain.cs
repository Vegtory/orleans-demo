namespace App.Api.GrainContracts;

/// <summary>
/// A presenter. The grain key is "{name}-{6hex}". Presenters create and manage
/// multiple-choice actions and choose which one is in focus for attendees.
/// API-level password protection is enforced before any of these are called.
/// </summary>
public interface IPresenterGrain : IGrainWithStringKey
{
    Task Initialize(string name);
    Task<PresenterView> GetState();

    /// <summary>Creates a new multiple-choice action and returns its id.</summary>
    Task<string> CreateMultipleChoice(string title, string[] options);

    /// <summary>Puts one of this presenter's actions in focus on the global presentation.</summary>
    Task SetActive(string actionId);

    /// <summary>
    /// Clears the global focus so attendees see no live action. Idempotent.
    /// </summary>
    Task ClearActive();

    Task<ResultsView> GetResults(string actionId);
}
