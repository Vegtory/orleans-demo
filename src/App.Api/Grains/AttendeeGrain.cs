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
        // Polling GetState is how an attendee "calls" the presentation, so use it
        // as the heartbeat that keeps them on the presenter's live roster.
        await GrainFactory
            .GetGrain<IAttendeeRosterGrain>(IAttendeeRosterGrain.GlobalKey)
            .Heartbeat(this.GetPrimaryKeyString(), _state.State.Name);

        var focus = await GrainFactory
            .GetGrain<IPresentationGrain>(IPresentationGrain.GlobalKey)
            .GetFocusInfo();

        if (focus is null)
        {
            return new AttendeeView(_state.State.Name, Focus: null, YourAnswer: null);
        }

        if (focus.Kind == ActionKind.ChargerSim)
        {
            return new AttendeeView(_state.State.Name, Focus: null, YourAnswer: null, ChargerSimActionId: focus.ActionId);
        }

        var action = GrainFactory.GetGrain<IMultipleChoiceGrain>(focus.ActionId);
        var question = await action.GetQuestion();
        var yourAnswer = await action.GetAnswer(this.GetPrimaryKeyString());
        return new AttendeeView(_state.State.Name, question, yourAnswer);
    }

    public async Task<bool> Answer(int optionIndex)
    {
        var focus = await GrainFactory
            .GetGrain<IPresentationGrain>(IPresentationGrain.GlobalKey)
            .GetFocusInfo();

        // Only multiple-choice actions accept an indexed answer.
        if (focus is not { Kind: ActionKind.MultipleChoice })
        {
            return false;
        }

        await GrainFactory.GetGrain<IMultipleChoiceGrain>(focus.ActionId)
            .Answer(this.GetPrimaryKeyString(), optionIndex);
        return true;
    }
}
