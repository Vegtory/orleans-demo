using App.Api.GrainContracts;

namespace App.Api.Grains;

/// <summary>
/// Persisted multiple-choice question. Answers are stored per attendee key so an
/// attendee can change their answer; tallies are derived on read.
/// </summary>
[GenerateSerializer]
public sealed class MultipleChoiceState
{
    [Id(0)]
    public string Title { get; set; } = string.Empty;

    [Id(1)]
    public string[] Options { get; set; } = [];

    [Id(2)]
    public Dictionary<string, int> Answers { get; set; } = new();
}

public sealed class MultipleChoiceGrain : Grain, IMultipleChoiceGrain
{
    private readonly IPersistentState<MultipleChoiceState> _state;

    public MultipleChoiceGrain(
        [PersistentState("multipleChoice", "store")] IPersistentState<MultipleChoiceState> state)
    {
        _state = state;
    }

    public async Task Configure(string title, string[] options)
    {
        _state.State.Title = title;
        _state.State.Options = options;
        await _state.WriteStateAsync();
    }

    public Task<QuestionView> GetQuestion() => Task.FromResult(
        new QuestionView(this.GetPrimaryKeyString(), _state.State.Title, _state.State.Options));

    public async Task Answer(string attendeeKey, int optionIndex)
    {
        if (optionIndex < 0 || optionIndex >= _state.State.Options.Length)
        {
            throw new ArgumentOutOfRangeException(
                nameof(optionIndex), optionIndex, "Option index is outside the question's options.");
        }

        _state.State.Answers[attendeeKey] = optionIndex;
        await _state.WriteStateAsync();
    }

    public Task<int?> GetAnswer(string attendeeKey) => Task.FromResult(
        _state.State.Answers.TryGetValue(attendeeKey, out var idx) ? idx : (int?)null);

    public Task<ResultsView> GetResults()
    {
        var counts = new int[_state.State.Options.Length];
        foreach (var idx in _state.State.Answers.Values)
        {
            if (idx >= 0 && idx < counts.Length)
            {
                counts[idx]++;
            }
        }

        return Task.FromResult(new ResultsView(
            this.GetPrimaryKeyString(),
            _state.State.Title,
            _state.State.Options,
            counts,
            _state.State.Answers.Count));
    }
}
