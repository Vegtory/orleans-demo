using App.Api.GrainContracts;
using Orleans.Runtime;

namespace App.Api.Grains;

/// <summary>A single batch command waiting in the attendee's background work queue.</summary>
[GenerateSerializer]
public sealed class QueuedBatchCommand
{
    [Id(0)] public BatchChargerCommandType Command { get; set; }

    /// <summary>How many chargers the command should target (selected from the aggregate snapshot).</summary>
    [Id(1)] public int Amount { get; set; }
}

/// <summary>Persisted state for one attendee's ChargerSim controller.</summary>
[GenerateSerializer]
public sealed class AttendeeChargerSimState
{
    [Id(0)] public string DisplayName { get; set; } = "";

    /// <summary>How many chargers this attendee has created (charger numbers 1..Count).</summary>
    [Id(1)] public int Count { get; set; }

    /// <summary>Set permanently once a kill request is received. Causes the grain to
    /// complete teardown on the next method entry and deactivate.</summary>
    [Id(2)] public bool Killed { get; set; }

    /// <summary>Chargers requested but not yet created. The background worker drains this to zero.</summary>
    [Id(3)] public int PendingCreate { get; set; }

    /// <summary>FIFO queue of batch commands the background worker still has to execute.</summary>
    [Id(4)] public List<QueuedBatchCommand> PendingCommands { get; set; } = new();
}

/// <summary>
/// One per attendee. The attendee's control surface for ChargerSim. Creation and
/// batch commands are accepted instantly — they are recorded as background work
/// (a desired charger count and a queue of commands) and carried out by a grain
/// timer that creates chargers in chunks and executes queued commands one at a
/// time. Single-charger commands and fleet reads stay synchronous. Create and
/// batch requests are rejected at submission time unless the action is active;
/// the cap is likewise enforced when a create request is accepted.
/// </summary>
public sealed class AttendeeChargerSimGrain : Grain, IAttendeeChargerSimGrain
{
    private const int CreateChunkSize = 250;
    private const int CommandChunkSize = 500;

    // The background worker fires this often while there is outstanding work. Each
    // tick creates at most one CreateChunkSize batch and executes one queued
    // command, keeping every tick short so it never blocks summary polls for long.
    private static readonly TimeSpan WorkerPeriod = TimeSpan.FromMilliseconds(250);

    private readonly IPersistentState<AttendeeChargerSimState> _state;
    private string _actionId = "";
    private string _attendeeId = "";
    private IGrainTimer? _worker;

    /// <summary>
    /// Cached copy of the action's active flag. Pulled once on activation and kept
    /// fresh by the action grain's push (see <see cref="SetActive"/>), so the
    /// <see cref="EnsureActive"/> gate never calls back into the hot action grain.
    /// </summary>
    private bool _active;

    public AttendeeChargerSimGrain(
        [PersistentState("attendeeChargerSim", "store")] IPersistentState<AttendeeChargerSimState> state)
    {
        _state = state;
    }

    public override async Task OnActivateAsync(CancellationToken cancellationToken)
    {
        // Key: "action-{actionId}/attendee-{attendeeId}".
        var parts = this.GetPrimaryKeyString().Split('/');
        _actionId = parts[0]["action-".Length..];
        _attendeeId = parts[1]["attendee-".Length..];
        await base.OnActivateAsync(cancellationToken);

        // If the grain was killed before it had a chance to deactivate (e.g. a
        // silo restart re-activated it from persisted state), finish teardown now.
        if (_state.State.Killed)
        {
            await SelfDestruct();
            return;
        }

        // Pull the action's active flag once so the gate is correct even if this
        // grain activated after the action went live (or after a silo restart). From
        // here on it's kept fresh by the action grain's push. One cold call per
        // activation, replacing an IsActive() call on every command.
        _active = await Action.IsActive();

        // If work was outstanding when the grain last deactivated (or the silo
        // restarted), pick it back up. The grain reactivates as soon as the UI's
        // summary poll touches it, so the worker resumes on its own.
        if (HasPendingWork)
            EnsureWorker();
    }

    public async Task Register(string displayName)
    {
        if (await HandleIfKilled()) return;

        if (_state.State.DisplayName != displayName && !string.IsNullOrWhiteSpace(displayName))
        {
            _state.State.DisplayName = displayName;
            await _state.WriteStateAsync();
        }

        await Action.RegisterAttendee(_attendeeId);
    }

    public Task SetActive(bool active)
    {
        _active = active;
        return Task.CompletedTask;
    }

    public async Task Kill()
    {
        if (_state.State.Killed) return;
        _state.State.Killed = true;
        await _state.WriteStateAsync();
        await SelfDestruct();
    }

    public async Task<int> CreateChargers(int amount)
    {
        if (await HandleIfKilled()) return _state.State.Count;
        EnsureActive();
        if (await Action.IsKillSwitchEnabled())
            throw new InvalidOperationException("Kill switch is engaged.");

        // Killed chargers do not count toward the cap, so the room available is
        // based on the number of currently live chargers (from the aggregate),
        // not the total ever created. We also reserve room for work already queued
        // so rapid requests can never blow past the cap. Charger numbers still
        // increase monotonically (driven by Count) so display ids stay unique.
        // The cap is the presenter-configurable per-attendee limit; read it on the
        // (cold) create path the same way the kill switch is read just above.
        var cap = await Action.GetMaxChargers();
        var summary = await Aggregate.GetSummary();
        var live = summary.NoSessionCount + summary.ActiveSessionCount + summary.PausedWithSessionCount;
        var room = cap - live - _state.State.PendingCreate;
        var toCreate = Math.Clamp(amount, 0, Math.Max(0, room));
        if (toCreate == 0)
        {
            return live + _state.State.PendingCreate;
        }

        // Record the request and let the background worker actually create them.
        _state.State.PendingCreate += toCreate;
        await _state.WriteStateAsync();

        await RecordEvent($"requested {toCreate:N0} chargers");
        EnsureWorker();
        return live + _state.State.PendingCreate;
    }

    public async Task KillMyChargers()
    {
        if (await HandleIfKilled()) return;

        // Queue a "kill everything still alive" batch. MaxChargers is an upper
        // bound on the selection; the worker resolves the actual live set from the
        // aggregate snapshot when it runs.
        await QueueCommand(BatchChargerCommandType.Kill, IAttendeeChargerSimGrain.MaxChargers);
    }

    public async Task SendBatchCommand(BatchChargerCommandType command, int amount)
    {
        if (await HandleIfKilled()) return;
        EnsureActive();

        var take = amount <= 0 ? IAttendeeChargerSimGrain.DefaultBatchSize : amount;
        await QueueCommand(command, take);
    }

    public Task<ChargerSimWorkStatus> GetWorkStatus() =>
        Task.FromResult(new ChargerSimWorkStatus(_state.State.PendingCreate, _state.State.PendingCommands.Count));

    public async Task<ChargerSnapshot?> CommandCharger(string chargerId, SingleChargerCommandType command)
    {
        if (await HandleIfKilled()) return null;
        EnsureActive();

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
        if (await HandleIfKilled()) return new ChargerFleetSummary { AttendeeName = _state.State.DisplayName };
        var summary = await Aggregate.GetSummary();
        summary.AttendeeName = _state.State.DisplayName;
        return summary;
    }

    public async Task<IReadOnlyList<ChargerCellState>> GetStateSample(int take)
    {
        if (await HandleIfKilled()) return [];
        return await Aggregate.GetStateSample(take);
    }

    public async Task<ChargerSnapshot?> GetCharger(string chargerId)
    {
        if (await HandleIfKilled()) return null;
        var charger = ResolveCharger(chargerId);
        return charger is null ? null : await charger.GetSnapshot();
    }

    public async Task<ChargerSnapshot?> GetRandomActiveCharger()
    {
        if (await HandleIfKilled()) return null;
        return await GetRandomInState(ChargerSimState.ActiveSession);
    }

    public async Task<ChargerSnapshot?> GetRandomPausedCharger()
    {
        if (await HandleIfKilled()) return null;
        return await GetRandomInState(ChargerSimState.PausedWithSession);
    }

    public async Task<IReadOnlyList<string>> GetChargerIds()
    {
        if (await HandleIfKilled()) return [];
        IReadOnlyList<string> ids = Enumerable.Range(1, _state.State.Count)
            .Select(ChargerSimKeys.DisplayId)
            .ToList();
        return ids;
    }

    // -- Background worker ------------------------------------------------------

    private bool HasPendingWork =>
        _state.State.PendingCreate > 0 || _state.State.PendingCommands.Count > 0;

    // Queues a batch command and makes sure the worker is running. The verb/filter
    // mapping lives in the worker (ExecuteNextCommand); here we just record intent.
    private async Task QueueCommand(BatchChargerCommandType command, int amount)
    {
        _state.State.PendingCommands.Add(new QueuedBatchCommand { Command = command, Amount = amount });
        await _state.WriteStateAsync();
        EnsureWorker();
    }

    // Starts the background worker if it isn't already running. KeepAlive holds the
    // activation open while work is outstanding; the timer disposes itself once the
    // queues drain so the grain can deactivate normally. Interleave is left off so
    // each tick runs as an ordinary (exclusive) grain turn and can't race the
    // request methods that mutate the same state.
    private void EnsureWorker()
    {
        _worker ??= this.RegisterGrainTimer(
            callback: static (self, _) => self.RunWorkerTick(),
            state: this,
            options: new GrainTimerCreationOptions
            {
                DueTime = TimeSpan.Zero,
                Period = WorkerPeriod,
                Interleave = false,
                KeepAlive = true
            });
    }

    private void StopWorker()
    {
        _worker?.Dispose();
        _worker = null;
    }

    // One unit of background work: create the next chunk of requested chargers,
    // then execute the next queued command. Stops the worker once both queues are
    // empty. Creation runs first so a command queued alongside a create acts on the
    // chargers that now exist.
    private async Task RunWorkerTick()
    {
        if (_state.State.Killed)
        {
            StopWorker();
            return;
        }

        if (_state.State.PendingCreate > 0)
        {
            await CreateNextChunk();
        }

        if (_state.State.PendingCommands.Count > 0)
        {
            await ExecuteNextCommand();
        }

        if (!HasPendingWork)
        {
            StopWorker();
        }
    }

    private async Task CreateNextChunk()
    {
        var toCreate = Math.Min(_state.State.PendingCreate, CreateChunkSize);
        var start = _state.State.Count + 1;
        var end = _state.State.Count + toCreate;

        var tasks = new List<Task>(toCreate);
        for (var i = start; i <= end; i++)
        {
            var charger = GrainFactory.GetGrain<IChargerGrain>(ChargerSimKeys.Charger(_actionId, _attendeeId, i));
            tasks.Add(charger.Initialize(ChargerSimKeys.DisplayId(i)));
        }

        await Task.WhenAll(tasks);

        _state.State.Count = end;
        _state.State.PendingCreate -= toCreate;
        await _state.WriteStateAsync();

        await RecordEvent($"created {toCreate:N0} chargers (now {_state.State.Count:N0})");
    }

    private async Task ExecuteNextCommand()
    {
        // Dequeue and persist before executing so a crash mid-flight can't replay
        // the command — at-most-once is the right tradeoff for fire-and-forget
        // fleet commands.
        var queued = _state.State.PendingCommands[0];
        _state.State.PendingCommands.RemoveAt(0);
        await _state.WriteStateAsync();

        var command = queued.Command;
        var take = queued.Amount <= 0 ? IAttendeeChargerSimGrain.DefaultBatchSize : queued.Amount;
        var (filter, verb) = command switch
        {
            BatchChargerCommandType.StartSessions => (ChargerSelectionFilter.WithoutSession, "started sessions on"),
            BatchChargerCommandType.StopCharging => (ChargerSelectionFilter.ActiveSessions, "stopped charging on"),
            BatchChargerCommandType.ResumeCharging => (ChargerSelectionFilter.PausedSessions, "resumed charging on"),
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
            BatchChargerCommandType.ResumeCharging => c.ResumeCharging(),
            BatchChargerCommandType.StopSessions => c.StopSession(),
            BatchChargerCommandType.LowerPowerUsage => c.LowerPowerUsage(),
            BatchChargerCommandType.IncreasePowerUsage => c.IncreasePowerUsage(),
            BatchChargerCommandType.RandomChaos => c.RandomChaos(),
            BatchChargerCommandType.Kill => c.Kill(),
            _ => Task.CompletedTask
        });

        await RecordEvent($"{verb} {numbers.Count:N0} chargers");
    }

    // -- Helpers ----------------------------------------------------------------

    private IChargerSimActionGrain Action =>
        GrainFactory.GetGrain<IChargerSimActionGrain>(ChargerSimKeys.Action(_actionId));

    private IAttendeeChargerAggregateGrain Aggregate =>
        GrainFactory.GetGrain<IAttendeeChargerAggregateGrain>(ChargerSimKeys.Aggregate(_actionId, _attendeeId));

    // Returns true (and initiates teardown) when the killed flag is set.
    // Call at the start of every public method: `if (await HandleIfKilled()) return;`
    private async Task<bool> HandleIfKilled()
    {
        if (!_state.State.Killed) return false;
        await SelfDestruct();
        return true;
    }

    // Unregisters any reminders, wipes persisted state, and schedules the grain
    // for deactivation. Safe to call multiple times (ClearStateAsync is idempotent,
    // DeactivateOnIdle is a hint the runtime ignores if already deactivating).
    private async Task SelfDestruct()
    {
        // Stop the background worker and drop any outstanding work — a killed sim
        // must not keep creating chargers or running commands.
        StopWorker();
        _state.State.PendingCreate = 0;
        _state.State.PendingCommands.Clear();

        // If this grain ever registers a reminder (implement IRemindable + call
        // RegisterOrUpdateReminder), unregister it here before clearing state so
        // the reminder store stays clean. Example:
        //   var r = await this.GetReminder("tick");
        //   if (r is not null) await this.UnregisterReminder(r);

        await _state.ClearStateAsync();
        DeactivateOnIdle();
    }

    // Reads the locally cached active flag (kept fresh by the action grain's push),
    // so the common command path never calls back into the hot action grain.
    private void EnsureActive()
    {
        if (!_active)
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
