using App.Api.GrainContracts;

namespace App.Api.Grains;

/// <summary>
/// Persisted attendee state: just the display name. The attendee's answers live
/// on the action grains (keyed by this attendee's grain key), so they are
/// resolved on demand rather than duplicated here.
/// </summary>
[GenerateSerializer]
public sealed class AttendeeState
{
    [Id(0)]
    public string Name { get; set; } = string.Empty;
}

public sealed class AttendeeGrain : Grain, IAttendeeGrain
{
    private readonly IPersistentState<AttendeeState> _state;

    public AttendeeGrain(
        [PersistentState("attendee", "store")] IPersistentState<AttendeeState> state)
    {
        _state = state;
    }

    public async Task Initialize(string name)
    {
        if (string.IsNullOrEmpty(_state.State.Name))
        {
            _state.State.Name = name;
            await _state.WriteStateAsync();
        }
    }

    public async Task<AttendeeView> GetState()
    {
        var presentation = GrainFactory.GetGrain<IPresentationGrain>(IPresentationGrain.GlobalKey);
        var focusId = await presentation.GetFocus();

        if (focusId is null)
        {
            return new AttendeeView(_state.State.Name, Focus: null, YourAnswer: null);
        }

        var kind = await presentation.GetFocusKind();
        if (kind == ActionKind.ChargerSim)
        {
            return new AttendeeView(_state.State.Name, Focus: null, YourAnswer: null, ChargerSimActionId: focusId);
        }

        var action = GrainFactory.GetGrain<IMultipleChoiceGrain>(focusId);
        var question = await action.GetQuestion();
        var yourAnswer = await action.GetAnswer(this.GetPrimaryKeyString());
        return new AttendeeView(_state.State.Name, question, yourAnswer);
    }

    public async Task<bool> Answer(int optionIndex)
    {
        var presentation = GrainFactory.GetGrain<IPresentationGrain>(IPresentationGrain.GlobalKey);
        var focusId = await presentation.GetFocus();

        // Only multiple-choice actions accept an indexed answer.
        if (focusId is null || await presentation.GetFocusKind() != ActionKind.MultipleChoice)
        {
            return false;
        }

        await GrainFactory.GetGrain<IMultipleChoiceGrain>(focusId)
            .Answer(this.GetPrimaryKeyString(), optionIndex);
        return true;
    }
}
