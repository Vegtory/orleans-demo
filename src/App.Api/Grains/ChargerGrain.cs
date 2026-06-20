using App.Api.GrainContracts;
using Orleans.Runtime;

namespace App.Api.Grains;

/// <summary>Persisted state for a single simulated charger.</summary>
[GenerateSerializer]
public sealed class ChargerState
{
    [Id(0)] public string ChargerId { get; set; } = "";
    [Id(1)] public string AttendeeId { get; set; } = "";
    [Id(2)] public ChargerSimState State { get; set; } = ChargerSimState.NoSession;
    [Id(3)] public string? ActiveSessionId { get; set; }
    [Id(4)] public double ActivePowerKw { get; set; }
    [Id(5)] public double MaxPowerKw { get; set; }
    [Id(6)] public double SessionKwh { get; set; }
    [Id(7)] public DateTimeOffset? SessionStartedAt { get; set; }
    [Id(8)] public DateTimeOffset LastUpdatedAt { get; set; }
    [Id(9)] public bool Killed { get; set; }
    [Id(10)] public long Version { get; set; }
    [Id(11)] public bool Initialized { get; set; }
}

/// <summary>
/// One simulated EV charger. Each instance owns a 30-second Orleans reminder
/// ("tick") that drives a small randomized state machine. After every tick and
/// every command it publishes its absolute contribution to its attendee's
/// aggregate grain — the aggregate is never built by polling chargers.
/// </summary>
public sealed class ChargerGrain : Grain, IChargerGrain, IRemindable
{
    private const string ReminderName = "tick";
    private static readonly TimeSpan TickPeriod = TimeSpan.FromSeconds(30);

    // Candidate max-power ratings (kW), one is picked per charger at creation.
    private static readonly double[] PowerRatings = [7.4, 11, 22, 50, 150];

    // Per-tick transition probabilities. With a ~30s tick, an end-of-session
    // chance of ~1/6 per tick puts the mean active-session length near 3 minutes.
    private const double StartChancePerTick = 0.30;
    private const double PauseChancePerTick = 0.12;
    private const double ResumeChancePerTick = 0.55;
    private const double EndSessionChancePerTick = 0.17;

    private readonly IPersistentState<ChargerState> _state;
    private string _actionId = "";

    public ChargerGrain(
        [PersistentState("charger", "store")] IPersistentState<ChargerState> state)
    {
        _state = state;
    }

    public override Task OnActivateAsync(CancellationToken cancellationToken)
    {
        var (actionId, attendeeId, _) = ChargerSimKeys.ParseChargerKey(this.GetPrimaryKeyString());
        _actionId = actionId;
        if (string.IsNullOrEmpty(_state.State.AttendeeId))
        {
            _state.State.AttendeeId = attendeeId;
        }

        return base.OnActivateAsync(cancellationToken);
    }

    public async Task Initialize(string displayId)
    {
        if (_state.State.Initialized)
        {
            return;
        }

        _state.State.Initialized = true;
        _state.State.ChargerId = displayId;
        _state.State.MaxPowerKw = PowerRatings[Random.Shared.Next(PowerRatings.Length)];
        _state.State.State = ChargerSimState.NoSession;
        _state.State.LastUpdatedAt = DateTimeOffset.UtcNow;
        _state.State.Version = 1;
        await _state.WriteStateAsync();

        // One durable 30-second reminder per charger.
        await this.RegisterOrUpdateReminder(ReminderName, TickPeriod, TickPeriod);

        await PublishContribution();
    }

    public Task<ChargerSnapshot> GetSnapshot() => Task.FromResult(Snapshot());

    public Task ReceiveReminder(string reminderName, TickStatus status) => SimulateTick();

    // -- Single-charger commands ------------------------------------------------

    public Task StartSession() => Mutate(s =>
    {
        if (s.Killed || s.State == ChargerSimState.ActiveSession) return;
        s.State = ChargerSimState.ActiveSession;
        s.ActiveSessionId = Guid.NewGuid().ToString("N")[..8];
        s.SessionStartedAt = DateTimeOffset.UtcNow;
        s.SessionKwh = 0;
        s.ActivePowerKw = InitialPower(s);
    });

    public Task StopCharging() => Mutate(s =>
    {
        if (s.Killed || s.State != ChargerSimState.ActiveSession) return;
        s.State = ChargerSimState.PausedWithSession;
        s.ActivePowerKw = 0;
    });

    public Task ResumeCharging() => Mutate(s =>
    {
        if (s.Killed || s.State != ChargerSimState.PausedWithSession) return;
        s.State = ChargerSimState.ActiveSession;
        s.ActivePowerKw = InitialPower(s);
    });

    public Task StopSession() => Mutate(s =>
    {
        if (s.Killed || s.State is ChargerSimState.NoSession) return;
        s.State = ChargerSimState.NoSession;
        s.ActiveSessionId = null;
        s.ActivePowerKw = 0;
        s.SessionStartedAt = null;
        s.SessionKwh = 0;
    });

    public Task LowerPowerUsage() => Mutate(s =>
    {
        if (s.Killed || s.State != ChargerSimState.ActiveSession) return;
        s.ActivePowerKw = Math.Max(1.0, s.ActivePowerKw * 0.6);
    });

    public Task IncreasePowerUsage() => Mutate(s =>
    {
        if (s.Killed || s.State != ChargerSimState.ActiveSession) return;
        s.ActivePowerKw = Math.Min(s.MaxPowerKw, Math.Max(1.0, s.ActivePowerKw * 1.4));
    });

    public Task RandomChaos() => Mutate(ApplyChaos);

    public async Task Kill()
    {
        if (!_state.State.Killed)
        {
            _state.State.Killed = true;
            _state.State.State = ChargerSimState.Killed;
            _state.State.ActiveSessionId = null;
            _state.State.ActivePowerKw = 0;
            _state.State.SessionKwh = 0;
            _state.State.SessionStartedAt = null;
            _state.State.Version++;
            _state.State.LastUpdatedAt = DateTimeOffset.UtcNow;
            await _state.WriteStateAsync();
            await PublishContribution();
        }

        // A killed charger no longer needs to tick.
        var reminder = await this.GetReminder(ReminderName);
        if (reminder is not null)
        {
            await this.UnregisterReminder(reminder);
        }

        // Nothing more will ever happen to a killed charger, so let it deactivate
        // and free its activation. Its final Killed contribution has already been
        // published to the aggregate.
        DeactivateOnIdle();
    }

    // -- Simulation -------------------------------------------------------------

    private Task SimulateTick()
    {
        if (_state.State.Killed)
        {
            return Task.CompletedTask;
        }

        return Mutate(s =>
        {
            switch (s.State)
            {
                case ChargerSimState.NoSession:
                    if (Random.Shared.NextDouble() < StartChancePerTick)
                    {
                        s.State = ChargerSimState.ActiveSession;
                        s.ActiveSessionId = Guid.NewGuid().ToString("N")[..8];
                        s.SessionStartedAt = DateTimeOffset.UtcNow;
                        s.SessionKwh = 0;
                        s.ActivePowerKw = InitialPower(s);
                    }
                    break;

                case ChargerSimState.ActiveSession:
                    // Energy accrued over the elapsed tick (30s = 1/120 h).
                    s.SessionKwh += s.ActivePowerKw * (TickPeriod.TotalHours);
                    // Power fluctuates ±10%, clamped to [1, max].
                    s.ActivePowerKw = Math.Clamp(
                        s.ActivePowerKw * (0.9 + Random.Shared.NextDouble() * 0.2), 1.0, s.MaxPowerKw);

                    var roll = Random.Shared.NextDouble();
                    if (roll < EndSessionChancePerTick)
                    {
                        s.State = ChargerSimState.NoSession;
                        s.ActiveSessionId = null;
                        s.ActivePowerKw = 0;
                        s.SessionStartedAt = null;
                        s.SessionKwh = 0;
                    }
                    else if (roll < EndSessionChancePerTick + PauseChancePerTick)
                    {
                        s.State = ChargerSimState.PausedWithSession;
                        s.ActivePowerKw = 0;
                    }
                    break;

                case ChargerSimState.PausedWithSession:
                    var r = Random.Shared.NextDouble();
                    if (r < ResumeChancePerTick)
                    {
                        s.State = ChargerSimState.ActiveSession;
                        s.ActivePowerKw = InitialPower(s);
                    }
                    else if (r < ResumeChancePerTick + EndSessionChancePerTick)
                    {
                        s.State = ChargerSimState.NoSession;
                        s.ActiveSessionId = null;
                        s.ActivePowerKw = 0;
                        s.SessionStartedAt = null;
                        s.SessionKwh = 0;
                    }
                    break;
            }
        });
    }

    private static void ApplyChaos(ChargerState s)
    {
        if (s.Killed) return;

        switch (Random.Shared.Next(6))
        {
            case 0: // start
                if (s.State == ChargerSimState.NoSession)
                {
                    s.State = ChargerSimState.ActiveSession;
                    s.ActiveSessionId = Guid.NewGuid().ToString("N")[..8];
                    s.SessionStartedAt = DateTimeOffset.UtcNow;
                    s.SessionKwh = 0;
                    s.ActivePowerKw = InitialPower(s);
                }
                break;
            case 1: // pause
                if (s.State == ChargerSimState.ActiveSession)
                {
                    s.State = ChargerSimState.PausedWithSession;
                    s.ActivePowerKw = 0;
                }
                break;
            case 2: // resume
                if (s.State == ChargerSimState.PausedWithSession)
                {
                    s.State = ChargerSimState.ActiveSession;
                    s.ActivePowerKw = InitialPower(s);
                }
                break;
            case 3: // stop session
                if (s.State is ChargerSimState.ActiveSession or ChargerSimState.PausedWithSession)
                {
                    s.State = ChargerSimState.NoSession;
                    s.ActiveSessionId = null;
                    s.ActivePowerKw = 0;
                    s.SessionStartedAt = null;
                    s.SessionKwh = 0;
                }
                break;
            case 4: // lower power
                if (s.State == ChargerSimState.ActiveSession)
                {
                    s.ActivePowerKw = Math.Max(1.0, s.ActivePowerKw * 0.6);
                }
                break;
            default: // do nothing
                break;
        }
    }

    private static double InitialPower(ChargerState s) =>
        Math.Clamp(s.MaxPowerKw * (0.6 + Random.Shared.NextDouble() * 0.4), 1.0, s.MaxPowerKw);

    // -- Plumbing ---------------------------------------------------------------

    // Applies a mutation, bumps the version + timestamp, persists, and publishes
    // the new absolute contribution to the attendee aggregate.
    private async Task Mutate(Action<ChargerState> mutate)
    {
        mutate(_state.State);
        _state.State.Version++;
        _state.State.LastUpdatedAt = DateTimeOffset.UtcNow;
        await _state.WriteStateAsync();
        await PublishContribution();
    }

    private Task PublishContribution()
    {
        var s = _state.State;
        var hasSession = s.State is ChargerSimState.ActiveSession or ChargerSimState.PausedWithSession;
        var contribution = new ChargerAggregateContribution
        {
            AttendeeId = s.AttendeeId,
            ChargerId = s.ChargerId,
            Version = s.Version,
            State = s.State,
            HasSession = hasSession,
            ActivePowerKw = s.ActivePowerKw,
            SessionKwh = s.SessionKwh,
            UpdatedAt = s.LastUpdatedAt
        };

        var aggregateKey = ChargerSimKeys.Aggregate(_actionId, s.AttendeeId);
        return GrainFactory.GetGrain<IAttendeeChargerAggregateGrain>(aggregateKey)
            .UpsertContribution(contribution);
    }

    private ChargerSnapshot Snapshot()
    {
        var s = _state.State;
        return new ChargerSnapshot(
            s.ChargerId, s.AttendeeId, s.State, s.ActiveSessionId,
            s.ActivePowerKw, s.MaxPowerKw, s.SessionKwh,
            s.SessionStartedAt, s.LastUpdatedAt, s.Killed, s.Version);
    }
}
