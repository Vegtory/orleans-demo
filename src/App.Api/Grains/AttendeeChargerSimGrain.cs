using App.Api.GrainContracts;

namespace App.Api.Grains;

/// <summary>Persisted state for one attendee's ChargerSim controller.</summary>
[GenerateSerializer]
public sealed class AttendeeChargerSimState
{
    [Id(0)] public string DisplayName { get; set; } = "";

    /// <summary>How many chargers this attendee has created (charger numbers 1..Count).</summary>
    [Id(1)] public int Count { get; set; }
}

/// <summary>
/// One per attendee. The attendee's control surface for ChargerSim: it creates
/// chargers (capped at 10,000), fans batch and single commands out to charger
/// grains, and delegates fleet reads to the aggregate grain. Commands are
/// rejected unless the action is active.
/// </summary>
public sealed class AttendeeChargerSimGrain : Grain, IAttendeeChargerSimGrain
{
    private const int CreateChunkSize = 250;
    private const int CommandChunkSize = 500;

    private readonly IPersistentState<AttendeeChargerSimState> _state;
    private string _actionId = "";
    private string _attendeeId = "";

    public AttendeeChargerSimGrain(
        [PersistentState("attendeeChargerSim", "store")] IPersistentState<AttendeeChargerSimState> state)
    {
        _state = state;
    }

    public override Task OnActivateAsync(CancellationToken cancellationToken)
    {
        // Key: "action-{actionId}/attendee-{attendeeId}".
        var parts = this.GetPrimaryKeyString().Split('/');
        _actionId = parts[0]["action-".Length..];
        _attendeeId = parts[1]["attendee-".Length..];
        return base.OnActivateAsync(cancellationToken);
    }

    public async Task Register(string displayName)
    {
        if (_state.State.DisplayName != displayName && !string.IsNullOrWhiteSpace(displayName))
        {
            _state.State.DisplayName = displayName;
            await _state.WriteStateAsync();
        }

        await Action.RegisterAttendee(_attendeeId);
    }

    public async Task<int> CreateChargers(int amount)
    {
        await EnsureActive();

        // Killed chargers do not count toward the cap, so the room available is
        // based on the number of currently live chargers (from the aggregate),
        // not the total ever created. Charger numbers still increase monotonically
        // so display ids stay unique.
        var summary = await Aggregate.GetSummary();
        var live = summary.NoSessionCount + summary.ActiveSessionCount + summary.PausedWithSessionCount;
        var room = IAttendeeChargerSimGrain.MaxChargers - live;
        var toCreate = Math.Clamp(amount, 0, room);
        if (toCreate == 0)
        {
            return _state.State.Count;
        }

        var start = _state.State.Count + 1;
        var end = _state.State.Count + toCreate;

        for (var n = start; n <= end; n += CreateChunkSize)
        {
            var chunkEnd = Math.Min(n + CreateChunkSize - 1, end);
            var tasks = new List<Task>(chunkEnd - n + 1);
            for (var i = n; i <= chunkEnd; i++)
            {
                var charger = GrainFactory.GetGrain<IChargerGrain>(ChargerSimKeys.Charger(_actionId, _attendeeId, i));
                tasks.Add(charger.Initialize(ChargerSimKeys.DisplayId(i)));
            }

            await Task.WhenAll(tasks);
        }

        _state.State.Count = end;
        await _state.WriteStateAsync();

        await RecordEvent($"created {toCreate:N0} chargers (now {_state.State.Count:N0})");
        return _state.State.Count;
    }

    public async Task KillMyChargers()
    {
        // Kill only the chargers that are still alive (selected from the aggregate
        // snapshot) so we don't reactivate already-killed, deactivated grains.
        // Each killed charger publishes its final Killed contribution.
        var ids = await Aggregate.SelectChargerIds(ChargerSelectionFilter.Any, _state.State.Count);
        var numbers = ids.Select(ChargerSimKeys.NumberFromDisplayId).Where(n => n > 0).ToList();

        await ForEachCharger(numbers, c => c.Kill());
        await RecordEvent($"killed all {numbers.Count:N0} chargers");
    }

    public async Task SendBatchCommand(BatchChargerCommandType command, int amount)
    {
        await EnsureActive();

        var take = amount <= 0 ? IAttendeeChargerSimGrain.DefaultBatchSize : amount;
        var (filter, verb) = command switch
        {
            BatchChargerCommandType.StartSessions => (ChargerSelectionFilter.WithoutSession, "started sessions on"),
            BatchChargerCommandType.StopCharging => (ChargerSelectionFilter.ActiveSessions, "stopped charging on"),
            BatchChargerCommandType.StopSessions => (ChargerSelectionFilter.ActiveOrPausedSessions, "stopped sessions on"),
            BatchChargerCommandType.LowerPowerUsage => (ChargerSelectionFilter.ActiveSessions, "lowered power usage on"),
            BatchChargerCommandType.IncreasePowerUsage => (ChargerSelectionFilter.ActiveSessions, "increased power usage on"),
            BatchChargerCommandType.RandomChaos => (ChargerSelectionFilter.Any, "unleashed chaos on"),
            BatchChargerCommandType.Kill => (ChargerSelectionFilter.Any, "killed"),
            _ => (ChargerSelectionFilter.Any, "touched")
        };

        // Selection comes from the aggregate snapshot — we never poll the chargers
        // to decide which ones to target.
        var ids = await Aggregate.SelectChargerIds(filter, take);
        var numbers = ids.Select(ChargerSimKeys.NumberFromDisplayId).Where(n => n > 0).ToList();

        await ForEachCharger(numbers, c => command switch
        {
            BatchChargerCommandType.StartSessions => c.StartSession(),
            BatchChargerCommandType.StopCharging => c.StopCharging(),
            BatchChargerCommandType.StopSessions => c.StopSession(),
            BatchChargerCommandType.LowerPowerUsage => c.LowerPowerUsage(),
            BatchChargerCommandType.IncreasePowerUsage => c.IncreasePowerUsage(),
            BatchChargerCommandType.RandomChaos => c.RandomChaos(),
            BatchChargerCommandType.Kill => c.Kill(),
            _ => Task.CompletedTask
        });

        await RecordEvent($"{verb} {numbers.Count:N0} chargers");
    }

    public async Task<ChargerSnapshot?> CommandCharger(string chargerId, SingleChargerCommandType command)
    {
        await EnsureActive();

        var charger = ResolveCharger(chargerId);
        if (charger is null)
        {
            return null;
        }

        Task action = command switch
        {
            SingleChargerCommandType.StartSession => charger.StartSession(),
            SingleChargerCommandType.PauseCharging => charger.StopCharging(),
            SingleChargerCommandType.ResumeCharging => charger.ResumeCharging(),
            SingleChargerCommandType.StopSession => charger.StopSession(),
            SingleChargerCommandType.LowerPower => charger.LowerPowerUsage(),
            SingleChargerCommandType.IncreasePower => charger.IncreasePowerUsage(),
            SingleChargerCommandType.Kill => charger.Kill(),
            _ => Task.CompletedTask
        };
        await action;

        return await charger.GetSnapshot();
    }

    public async Task<ChargerFleetSummary> GetSummary()
    {
        var summary = await Aggregate.GetSummary();
        summary.AttendeeName = _state.State.DisplayName;
        return summary;
    }

    public async Task<ChargerSnapshot?> GetCharger(string chargerId)
    {
        var charger = ResolveCharger(chargerId);
        return charger is null ? null : await charger.GetSnapshot();
    }

    public Task<ChargerSnapshot?> GetRandomActiveCharger() => GetRandomInState(ChargerSimState.ActiveSession);
    public Task<ChargerSnapshot?> GetRandomPausedCharger() => GetRandomInState(ChargerSimState.PausedWithSession);

    public Task<IReadOnlyList<string>> GetChargerIds()
    {
        IReadOnlyList<string> ids = Enumerable.Range(1, _state.State.Count)
            .Select(ChargerSimKeys.DisplayId)
            .ToList();
        return Task.FromResult(ids);
    }

    // -- Helpers ----------------------------------------------------------------

    private IChargerSimActionGrain Action =>
        GrainFactory.GetGrain<IChargerSimActionGrain>(ChargerSimKeys.Action(_actionId));

    private IAttendeeChargerAggregateGrain Aggregate =>
        GrainFactory.GetGrain<IAttendeeChargerAggregateGrain>(ChargerSimKeys.Aggregate(_actionId, _attendeeId));

    private async Task EnsureActive()
    {
        if (!await Action.IsActive())
        {
            throw new InvalidOperationException("ChargerSim is not active.");
        }
    }

    private async Task<ChargerSnapshot?> GetRandomInState(ChargerSimState state)
    {
        var id = await Aggregate.GetRandomChargerInState(state);
        if (id is null)
        {
            return null;
        }

        var charger = ResolveCharger(id);
        return charger is null ? null : await charger.GetSnapshot();
    }

    private IChargerGrain? ResolveCharger(string chargerId)
    {
        var number = ChargerSimKeys.NumberFromDisplayId(chargerId);
        if (number < 1 || number > _state.State.Count)
        {
            return null;
        }

        return GrainFactory.GetGrain<IChargerGrain>(ChargerSimKeys.Charger(_actionId, _attendeeId, number));
    }

    private Task ForEachCharger(int from, int to, Func<IChargerGrain, Task> command) =>
        ForEachCharger(Enumerable.Range(from, Math.Max(0, to - from + 1)).ToList(), command);

    private async Task ForEachCharger(IReadOnlyList<int> numbers, Func<IChargerGrain, Task> command)
    {
        for (var i = 0; i < numbers.Count; i += CommandChunkSize)
        {
            var chunk = numbers.Skip(i).Take(CommandChunkSize);
            var tasks = chunk.Select(n =>
                command(GrainFactory.GetGrain<IChargerGrain>(ChargerSimKeys.Charger(_actionId, _attendeeId, n))));
            await Task.WhenAll(tasks);
        }
    }

    private Task RecordEvent(string suffix)
    {
        var who = string.IsNullOrWhiteSpace(_state.State.DisplayName) ? "An attendee" : _state.State.DisplayName;
        return Action.RecordEvent($"{who} {suffix}");
    }
}
