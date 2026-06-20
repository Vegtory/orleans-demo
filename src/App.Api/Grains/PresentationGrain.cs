using App.Api.GrainContracts;

namespace App.Api.Grains;

/// <summary>
/// Persisted focus pointer for the single global presentation. Uses the storage
/// provider named "store" (in-memory in Local mode, Azure Tables otherwise).
/// </summary>
[GenerateSerializer]
public sealed class PresentationState
{
    [Id(0)]
    public string? FocusedActionId { get; set; }

    [Id(1)]
    public ActionKind FocusedKind { get; set; }
}

public sealed class PresentationGrain : Grain, IPresentationGrain
{
    private readonly IPersistentState<PresentationState> _state;

    public PresentationGrain(
        [PersistentState("presentation", "store")] IPersistentState<PresentationState> state)
    {
        _state = state;
    }

    public async Task SetFocus(string actionId, ActionKind kind = ActionKind.MultipleChoice)
    {
        _state.State.FocusedActionId = actionId;
        _state.State.FocusedKind = kind;
        await _state.WriteStateAsync();
    }

    public async Task ClearFocus()
    {
        _state.State.FocusedActionId = null;
        await _state.WriteStateAsync();
    }

    public Task<string?> GetFocus() => Task.FromResult(_state.State.FocusedActionId);

    public Task<PresentationFocus?> GetFocusInfo() => Task.FromResult(
        _state.State.FocusedActionId is { } id
            ? new PresentationFocus(id, _state.State.FocusedKind)
            : null);
}
