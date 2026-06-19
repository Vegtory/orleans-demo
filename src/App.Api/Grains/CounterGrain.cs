using App.Api.GrainContracts;

namespace App.Api.Grains;

/// <summary>
/// Persisted counter state. The storage provider named "counterStore" is
/// configured for both Local (in-memory) and AzureStorage modes, so this
/// grain never changes when switching clustering/persistence backends.
/// </summary>
[GenerateSerializer]
public sealed class CounterState
{
    [Id(0)]
    public int Value { get; set; }
}

public sealed class CounterGrain : Grain, ICounterGrain
{
    private readonly IPersistentState<CounterState> _state;

    public CounterGrain(
        [PersistentState("counter", "counterStore")] IPersistentState<CounterState> state)
    {
        _state = state;
    }

    public Task<int> Get() => Task.FromResult(_state.State.Value);

    public async Task<int> Increment()
    {
        _state.State.Value++;
        await _state.WriteStateAsync();
        return _state.State.Value;
    }

    public async Task Reset()
    {
        _state.State.Value = 0;
        await _state.WriteStateAsync();
    }
}
