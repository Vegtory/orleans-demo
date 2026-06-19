using App.Api.GrainContracts;

namespace App.Api.Grains;

/// <summary>
/// Persisted presenter state: the presenter's display name and the actions they
/// have created (id + title + option count for listing).
/// </summary>
[GenerateSerializer]
public sealed class PresenterState
{
    [Id(0)]
    public string Name { get; set; } = string.Empty;

    [Id(1)]
    public List<ActionSummary> Actions { get; set; } = new();

    [Id(2)]
    public string? ActiveActionId { get; set; }
}

public sealed class PresenterGrain : Grain, IPresenterGrain
{
    private readonly IPersistentState<PresenterState> _state;

    public PresenterGrain(
        [PersistentState("presenter", "store")] IPersistentState<PresenterState> state)
    {
        _state = state;
    }

    public async Task Initialize(string name)
    {
        // Only set the name the first time; re-initialization keeps existing actions.
        if (string.IsNullOrEmpty(_state.State.Name))
        {
            _state.State.Name = name;
            await _state.WriteStateAsync();
        }
    }

    public Task<PresenterView> GetState() => Task.FromResult(new PresenterView(
        _state.State.Name,
        _state.State.Actions.ToArray(),
        _state.State.ActiveActionId));

    public async Task<string> CreateMultipleChoice(string title, string[] options)
    {
        var actionId = Guid.NewGuid().ToString("N");
        await GrainFactory.GetGrain<IMultipleChoiceGrain>(actionId).Configure(title, options);

        _state.State.Actions.Add(new ActionSummary(actionId, title, options.Length));
        await _state.WriteStateAsync();
        return actionId;
    }

    public async Task SetActive(string actionId)
    {
        if (_state.State.Actions.All(a => a.Id != actionId))
        {
            throw new InvalidOperationException("Unknown action id for this presenter.");
        }

        await GrainFactory.GetGrain<IPresentationGrain>(IPresentationGrain.GlobalKey).SetFocus(actionId);
        _state.State.ActiveActionId = actionId;
        await _state.WriteStateAsync();
    }

    public Task<ResultsView> GetResults(string actionId)
    {
        if (_state.State.Actions.All(a => a.Id != actionId))
        {
            throw new InvalidOperationException("Unknown action id for this presenter.");
        }

        return GrainFactory.GetGrain<IMultipleChoiceGrain>(actionId).GetResults();
    }
}
