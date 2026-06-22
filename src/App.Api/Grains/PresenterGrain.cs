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

    public async Task<string> CreateChargerSim(string title)
    {
        // Every ChargerSim uses the same well-known action id, so all presenters
        // share control of the one fleet sim (same chargers, aggregates, dashboard)
        // rather than each spinning up an isolated copy.
        var actionId = ChargerSimKeys.FleetActionId;

        // The ChargerSim action grain is created lazily on activation; we only
        // need to record the summary so the presenter can list/activate it. Guard
        // against listing the shared sim twice if it was already created here.
        if (_state.State.Actions.All(a => a.Id != actionId))
        {
            _state.State.Actions.Add(new ActionSummary(actionId, title, 0, ActionKind.ChargerSim));
            await _state.WriteStateAsync();
        }

        return actionId;
    }

    public async Task SetActive(string actionId)
    {
        var action = _state.State.Actions.FirstOrDefault(a => a.Id == actionId)
            ?? throw new InvalidOperationException("Unknown action id for this presenter.");

        // Activating a ChargerSim action also flips its action grain on so
        // attendees can use their control panels.
        if (action.Kind == ActionKind.ChargerSim)
        {
            await GrainFactory.GetGrain<IChargerSimActionGrain>(ChargerSimKeys.Action(actionId)).Activate();
        }

        await GrainFactory.GetGrain<IPresentationGrain>(IPresentationGrain.GlobalKey).SetFocus(actionId, action.Kind);
        _state.State.ActiveActionId = actionId;
        await _state.WriteStateAsync();
    }

    public async Task ClearActive()
    {
        // If a ChargerSim action was live, deactivate its action grain too so
        // attendee controls lock out.
        var active = _state.State.Actions.FirstOrDefault(a => a.Id == _state.State.ActiveActionId);
        if (active is { Kind: ActionKind.ChargerSim })
        {
            await GrainFactory.GetGrain<IChargerSimActionGrain>(ChargerSimKeys.Action(active.Id)).Deactivate();
        }

        await GrainFactory.GetGrain<IPresentationGrain>(IPresentationGrain.GlobalKey).ClearFocus();
        _state.State.ActiveActionId = null;
        await _state.WriteStateAsync();
    }

    public async Task RemoveAction(string actionId)
    {
        var action = _state.State.Actions.FirstOrDefault(a => a.Id == actionId)
            ?? throw new InvalidOperationException("Unknown action id for this presenter.");

        if (_state.State.ActiveActionId == actionId)
        {
            await ClearActive();
        }

        if (action.Kind == ActionKind.ChargerSim)
        {
            await GrainFactory.GetGrain<IChargerSimActionGrain>(ChargerSimKeys.Action(actionId)).Delete();
        }
        else
        {
            await GrainFactory.GetGrain<IMultipleChoiceGrain>(actionId).Delete();
        }

        _state.State.Actions.RemoveAll(a => a.Id == actionId);
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
