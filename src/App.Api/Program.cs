using System.Text;
using System.Threading.RateLimiting;
using App.Api.GrainContracts;
using App.Api.Observability;
using Microsoft.AspNetCore.RateLimiting;
using Orleans.Configuration;

var builder = WebApplication.CreateBuilder(args);

// ---------------------------------------------------------------------------
// Orleans silo, co-hosted in this same ASP.NET Core process.
//
// Clustering/persistence are selected by ORLEANS_CLUSTERING:
//   Local        -> localhost clustering + in-memory grain storage (dev / single replica)
//   AzureStorage -> Azure Table clustering + Azure Table grain persistence
//
// The grain storage provider is always named "store" so grain code is identical
// regardless of the backend.
// ---------------------------------------------------------------------------
// Note: use a non-"Orleans" config key — Orleans 10 reserves the "Orleans"
// configuration section for its own provider configuration.
var clustering = builder.Configuration["ORLEANS_CLUSTERING"]
    ?? builder.Configuration["OrleansClustering"]
    ?? "Local";

builder.Host.UseOrleans(silo =>
{
    if (string.Equals(clustering, "AzureStorage", StringComparison.OrdinalIgnoreCase))
    {
        var connectionString = builder.Configuration["ORLEANS_AZURE_STORAGE_CONNECTION_STRING"];
        if (string.IsNullOrWhiteSpace(connectionString))
        {
            throw new InvalidOperationException(
                "ORLEANS_CLUSTERING=AzureStorage requires ORLEANS_AZURE_STORAGE_CONNECTION_STRING to be set.");
        }

        silo.UseAzureStorageClustering(options =>
            options.TableServiceClient = new Azure.Data.Tables.TableServiceClient(connectionString));

        silo.AddAzureTableGrainStorage("store", options =>
            options.TableServiceClient = new Azure.Data.Tables.TableServiceClient(connectionString));

        // Durable reminders for ChargerSim's per-charger 30s ticks.
        silo.UseAzureTableReminderService(options =>
            options.TableServiceClient = new Azure.Data.Tables.TableServiceClient(connectionString));
    }
    else
    {
        // Local development / single-replica demo only.
        silo.UseLocalhostClustering();
        silo.AddMemoryGrainStorage("store");
        silo.UseInMemoryReminderService();
    }

    // ChargerSim registers reminders on a 30-second period; Orleans' default
    // minimum reminder period is one minute, so lower it for the demo.
    silo.Configure<ReminderOptions>(options =>
        options.MinimumReminderPeriod = TimeSpan.FromSeconds(15));

    // -----------------------------------------------------------------------
    // Debug/demo cluster observability (powers the presenter "cluster activity"
    // visualization). An outgoing call filter records grain->grain calls into a
    // per-silo queue; a grain service flushes that queue every 100ms into a
    // cluster-wide rolling recorder. See App.Api.Observability.
    // -----------------------------------------------------------------------
    silo.Services.AddSingleton<LocalCallTraceQueue>();
    silo.Services.AddSingleton<CallTraceSuppression>();
    silo.Services.AddSingleton<CallTraceRuntimeSwitch>();
    silo.AddOutgoingGrainCallFilter<GrainCallTraceFilter>();
    silo.AddGrainService<CallTraceReporterGrainService>();
});

// Static presenter password. Every presenter request must send it in the
// X-Presenter-Password header. Override via Presenter:Password or PRESENTER_PASSWORD.
var presenterPassword = builder.Configuration["Presenter:Password"]
    ?? builder.Configuration["PRESENTER_PASSWORD"]
    ?? "presenting-secrets!";

// ---------------------------------------------------------------------------
// Web services.
// ---------------------------------------------------------------------------

// Fixed-window rate limiting for public safety, applied to /api routes.
//
// The limit is *per client* rather than a single global bucket. The frontend
// polls several endpoints on short intervals — the presenter cluster view alone
// polls 3 endpoints every 500ms (~360 req/min), plus the presenter state/results
// poll — so a single browser legitimately generates ~400+ requests/minute. A
// shared global bucket meant one user (or one tab) instantly exhausted the
// budget for everyone, producing spurious 429s.
//
// We partition on the X-Session-Id header (a stable per-browser id the SPA
// sends on every /api call), falling back to remote IP when it's absent. This
// keeps attendees behind a single venue NAT — who would otherwise share one IP
// partition and trip the limit collectively — in their own buckets.
//
// PermitLimit/Window are configurable so the limit can be tuned without a code
// change. The default leaves comfortable headroom over a single browser's peak
// polling traffic.
var rateLimitPermits = builder.Configuration.GetValue<int?>("RateLimiting:PermitLimit") ?? 600;
var rateLimitWindowSeconds = builder.Configuration.GetValue<int?>("RateLimiting:WindowSeconds") ?? 60;

builder.Services.AddRateLimiter(options =>
{
    options.RejectionStatusCode = StatusCodes.Status429TooManyRequests;
    options.AddPolicy("api", httpContext =>
    {
        var sessionId = httpContext.Request.Headers["X-Session-Id"].ToString();
        var clientKey = !string.IsNullOrWhiteSpace(sessionId)
            ? $"sid:{sessionId}"
            : $"ip:{httpContext.Connection.RemoteIpAddress?.ToString() ?? "unknown"}";
        return RateLimitPartition.GetFixedWindowLimiter(clientKey, _ => new FixedWindowRateLimiterOptions
        {
            PermitLimit = rateLimitPermits,
            Window = TimeSpan.FromSeconds(rateLimitWindowSeconds),
            QueueLimit = 0,
            QueueProcessingOrder = QueueProcessingOrder.OldestFirst
        });
    });
});

// Swagger/OpenAPI is only wired up in Development (see below).
builder.Services.AddEndpointsApiExplorer();
builder.Services.AddSwaggerGen();

var app = builder.Build();

// OpenAPI/Swagger UI is exposed in Development only — never in production.
if (app.Environment.IsDevelopment())
{
    app.UseSwagger();
    app.UseSwaggerUI();
}

app.UseRateLimiter();

// Serve the SvelteKit static build from wwwroot.
app.UseDefaultFiles();
app.UseStaticFiles();

// ---------------------------------------------------------------------------
// API endpoints. Grouped under /api and rate limited.
// ---------------------------------------------------------------------------
var api = app.MapGroup("/api").RequireRateLimiting("api");

api.MapGet("/health", () => Results.Ok(new { status = "ok" }));

// Turns a display name into a stable "{slug}-{6hex}" grain key.
static string MakeKey(string name)
{
    var slug = new StringBuilder();
    foreach (var ch in name.Trim().ToLowerInvariant())
    {
        if (ch is >= 'a' and <= 'z' or >= '0' and <= '9')
        {
            slug.Append(ch);
        }
        else if (ch == ' ' && slug.Length > 0 && slug[^1] != '-')
        {
            slug.Append('-');
        }
    }

    if (slug.Length == 0)
    {
        slug.Append("anon");
    }

    return $"{slug}-{Guid.NewGuid().ToString("N")[..6]}";
}

bool PresenterOk(HttpRequest request) =>
    request.Headers["X-Presenter-Password"] == presenterPassword;

// --- Presenter (password-protected) -----------------------------------------

api.MapPost("/presenter", async (NameRequest body, HttpRequest req, IGrainFactory grains) =>
{
    if (!PresenterOk(req)) return Results.Unauthorized();
    if (string.IsNullOrWhiteSpace(body.Name)) return Results.BadRequest(new { error = "name is required" });

    var key = MakeKey(body.Name);
    await grains.GetGrain<IPresenterGrain>(key).Initialize(body.Name);
    return Results.Ok(new { key });
});

api.MapGet("/presenter/{key}", async (string key, HttpRequest req, IGrainFactory grains) =>
{
    if (!PresenterOk(req)) return Results.Unauthorized();
    return Results.Ok(await grains.GetGrain<IPresenterGrain>(key).GetState());
});

// The live attendee roster: everyone who has called the presentation within the
// active window. Global (not tied to a presenter key), so it lives on a literal
// route that takes precedence over /presenter/{key}.
api.MapGet("/presenter/attendees", async (HttpRequest req, IGrainFactory grains) =>
{
    if (!PresenterOk(req)) return Results.Unauthorized();
    var roster = await grains
        .GetGrain<IAttendeeRosterGrain>(IAttendeeRosterGrain.GlobalKey)
        .GetActive();
    return Results.Ok(roster);
});

// Recent attendee reactions for the presenter to animate. Global (not tied to a
// presenter key), like the roster. The presenter passes the last sequence it has
// already shown via ?since= and gets back only newer events plus the new cursor;
// omitting it on the first poll returns just the cursor so old reactions aren't
// replayed. Literal route, so it takes precedence over /presenter/{key}.
api.MapGet("/presenter/reactions", async (long? since, HttpRequest req, IGrainFactory grains) =>
{
    if (!PresenterOk(req)) return Results.Unauthorized();
    var feed = await grains
        .GetGrain<IReactionsGrain>(IReactionsGrain.GlobalKey)
        .GetSince(since);
    return Results.Ok(feed);
});

api.MapPost("/presenter/{key}/actions", async (string key, CreateActionRequest body, HttpRequest req, IGrainFactory grains) =>
{
    if (!PresenterOk(req)) return Results.Unauthorized();
    if (string.IsNullOrWhiteSpace(body.Title)) return Results.BadRequest(new { error = "title is required" });

    var options = (body.Options ?? [])
        .Select(o => o?.Trim() ?? string.Empty)
        .Where(o => o.Length > 0)
        .ToArray();
    if (options.Length < 2) return Results.BadRequest(new { error = "at least two options are required" });

    var actionId = await grains.GetGrain<IPresenterGrain>(key).CreateMultipleChoice(body.Title.Trim(), options);
    return Results.Ok(new { actionId });
});

api.MapPost("/presenter/{key}/chargersim", async (string key, CreateChargerSimRequest body, HttpRequest req, IGrainFactory grains) =>
{
    if (!PresenterOk(req)) return Results.Unauthorized();
    var title = string.IsNullOrWhiteSpace(body.Title) ? "Charger fleet simulation" : body.Title.Trim();
    var actionId = await grains.GetGrain<IPresenterGrain>(key).CreateChargerSim(title);
    return Results.Ok(new { actionId });
});

// Presenter ChargerSim dashboard: global + per-attendee summaries + event ticker,
// served from the action grain (which reads aggregate grains, never chargers).
api.MapGet("/presenter/{key}/chargersim/{actionId}/dashboard", async (string key, string actionId, HttpRequest req, IGrainFactory grains) =>
{
    if (!PresenterOk(req)) return Results.Unauthorized();
    var dashboard = await grains
        .GetGrain<IChargerSimActionGrain>(ChargerSimKeys.Action(actionId))
        .GetDashboard();
    return Results.Ok(dashboard);
});

api.MapPost("/presenter/{key}/chargersim/{actionId}/killswitch", async (string key, string actionId, KillSwitchRequest body, HttpRequest req, IGrainFactory grains) =>
{
    if (!PresenterOk(req)) return Results.Unauthorized();
    await grains.GetGrain<IChargerSimActionGrain>(ChargerSimKeys.Action(actionId)).SetKillSwitch(body.Enabled);
    return Results.Ok(new { killSwitchEnabled = body.Enabled });
});

api.MapPost("/presenter/{key}/actions/{actionId}/activate", async (string key, string actionId, HttpRequest req, IGrainFactory grains) =>
{
    if (!PresenterOk(req)) return Results.Unauthorized();
    try
    {
        await grains.GetGrain<IPresenterGrain>(key).SetActive(actionId);
        return Results.Ok(new { actionId, active = true });
    }
    catch (InvalidOperationException ex)
    {
        return Results.BadRequest(new { error = ex.Message });
    }
});

api.MapPost("/presenter/{key}/deactivate", async (string key, HttpRequest req, IGrainFactory grains) =>
{
    if (!PresenterOk(req)) return Results.Unauthorized();
    await grains.GetGrain<IPresenterGrain>(key).ClearActive();
    return Results.Ok(new { active = false });
});

api.MapGet("/presenter/{key}/actions/{actionId}/results", async (string key, string actionId, HttpRequest req, IGrainFactory grains) =>
{
    if (!PresenterOk(req)) return Results.Unauthorized();
    try
    {
        return Results.Ok(await grains.GetGrain<IPresenterGrain>(key).GetResults(actionId));
    }
    catch (InvalidOperationException ex)
    {
        return Results.BadRequest(new { error = ex.Message });
    }
});

api.MapDelete("/presenter/{key}/actions/{actionId}", async (string key, string actionId, HttpRequest req, IGrainFactory grains) =>
{
    if (!PresenterOk(req)) return Results.Unauthorized();
    try
    {
        await grains.GetGrain<IPresenterGrain>(key).RemoveAction(actionId);
        return Results.Ok(new { removed = true });
    }
    catch (InvalidOperationException ex)
    {
        return Results.BadRequest(new { error = ex.Message });
    }
});

// --- Cluster activity (presenter-only, debug/demo visualization) -------------

// Single poll endpoint backing the live cluster view: a snapshot of every active
// activation (and the silo it lives on), the most recent grain-to-grain calls,
// and the current cluster-wide tracing toggle. Consolidated into one request so
// the view polls once per tick instead of three times. The three grain reads run
// concurrently.
api.MapGet("/cluster/live", async (HttpRequest req, IGrainFactory grains) =>
{
    if (!PresenterOk(req)) return Results.Unauthorized();

    var inventory = grains.GetGrain<IActivationInventoryGrain>(0);
    // Idempotent: ensures the polling timer is running before we read.
    await inventory.Start();

    var activationsTask = inventory.GetSnapshot();
    var callsTask = grains.GetGrain<IClusterCallRecorderGrain>(0).GetRecent();
    var tracingTask = grains.GetGrain<IClusterTraceControlGrain>(0).GetState();
    await Task.WhenAll(activationsTask, callsTask, tracingTask);

    return Results.Ok(new
    {
        activations = await activationsTask,
        calls = await callsTask,
        tracing = new { enabled = (await tracingTask).Enabled }
    });
});

// Cluster-wide call-tracing toggle. Disabling it makes the filter stop recording
// (a local volatile read per call) and the reporter stop flushing; the change
// propagates to every silo within the ~100ms poll interval. The current value is
// surfaced via /cluster/live above.
api.MapPost("/cluster/tracing", async (TraceToggleRequest body, HttpRequest req, IGrainFactory grains) =>
{
    if (!PresenterOk(req)) return Results.Unauthorized();

    await grains.GetGrain<IClusterTraceControlGrain>(0).SetEnabled(body.Enabled);
    return Results.Ok(new { enabled = body.Enabled });
});

// --- Attendee (no password) --------------------------------------------------

api.MapPost("/attendee", async (NameRequest body, IGrainFactory grains) =>
{
    if (string.IsNullOrWhiteSpace(body.Name)) return Results.BadRequest(new { error = "name is required" });

    var key = MakeKey(body.Name);
    await grains.GetGrain<IAttendeeGrain>(key).Initialize(body.Name);
    return Results.Ok(new { key });
});

api.MapGet("/attendee/{key}", async (string key, IGrainFactory grains) =>
    Results.Ok(await grains.GetGrain<IAttendeeGrain>(key).GetState()));

// Fire-and-forget emoji reaction. Pushed onto the global reaction feed, where the
// presenter view polls for it and animates it floating up. No password: any
// attendee can react. Unknown kinds are ignored by the grain.
api.MapPost("/attendee/{key}/reaction", async (string key, ReactionRequest body, IGrainFactory grains) =>
{
    var kind = body.Kind?.Trim() ?? string.Empty;
    if (!IReactionsGrain.AllowedKinds.Contains(kind))
    {
        return Results.BadRequest(new { error = $"unknown reaction '{body.Kind}'" });
    }

    // Forward through the attendee's own grain so the emote becomes a real
    // attendee -> reactions grain hop, visible in the cluster call view.
    await grains.GetGrain<IAttendeeGrain>(key).React(kind);
    return Results.Ok(new { accepted = true });
});

api.MapPost("/attendee/{key}/answer", async (string key, AnswerRequest body, IGrainFactory grains) =>
{
    try
    {
        var accepted = await grains.GetGrain<IAttendeeGrain>(key).Answer(body.OptionIndex);
        return accepted
            ? Results.Ok(new { accepted = true })
            : Results.Conflict(new { error = "no action is currently in focus" });
    }
    catch (ArgumentOutOfRangeException)
    {
        return Results.BadRequest(new { error = "optionIndex is out of range" });
    }
});

// --- Attendee ChargerSim (no password) ---------------------------------------
//
// All routes go through the attendee's controller grain, which enforces that the
// action is active and that the attendee owns the targeted chargers. The grain
// key is "action-{actionId}/attendee-{attendeeKey}".

static IAttendeeChargerSimGrain ChargerSimAttendee(IGrainFactory grains, string actionId, string attendeeKey) =>
    grains.GetGrain<IAttendeeChargerSimGrain>(ChargerSimKeys.Attendee(actionId, attendeeKey));

static IResult ChargerSimError(InvalidOperationException ex) =>
    Results.Conflict(new { error = ex.Message });

// Idempotent join: records the attendee's name and registers them with the action.
api.MapPost("/chargersim/{actionId}/attendee/{key}/register", async (string actionId, string key, NameRequest body, IGrainFactory grains) =>
{
    await ChargerSimAttendee(grains, actionId, key).Register(body.Name ?? "");
    return Results.Ok(new { registered = true });
});

api.MapGet("/chargersim/{actionId}/attendee/{key}/summary", async (string actionId, string key, IGrainFactory grains) =>
    Results.Ok(await ChargerSimAttendee(grains, actionId, key).GetSummary()));

// Outstanding background work (chargers still being created, commands still queued),
// so the attendee UI can show a "working…" indicator while the worker drains.
api.MapGet("/chargersim/{actionId}/attendee/{key}/work", async (string actionId, string key, IGrainFactory grains) =>
    Results.Ok(await ChargerSimAttendee(grains, actionId, key).GetWorkStatus()));

api.MapPost("/chargersim/{actionId}/attendee/{key}/create", async (string actionId, string key, AmountRequest body, IGrainFactory grains) =>
{
    try
    {
        var total = await ChargerSimAttendee(grains, actionId, key).CreateChargers(body.Amount);
        return Results.Ok(new { total });
    }
    catch (InvalidOperationException ex) { return ChargerSimError(ex); }
});

api.MapPost("/chargersim/{actionId}/attendee/{key}/batch", async (string actionId, string key, BatchRequest body, IGrainFactory grains) =>
{
    if (!Enum.TryParse<BatchChargerCommandType>(body.Command, ignoreCase: true, out var command))
    {
        return Results.BadRequest(new { error = $"unknown command '{body.Command}'" });
    }

    try
    {
        var amount = body.Amount <= 0 ? IAttendeeChargerSimGrain.DefaultBatchSize : body.Amount;
        await ChargerSimAttendee(grains, actionId, key).SendBatchCommand(command, amount);
        return Results.Ok(new { accepted = true });
    }
    catch (InvalidOperationException ex) { return ChargerSimError(ex); }
});

api.MapPost("/chargersim/{actionId}/attendee/{key}/kill", async (string actionId, string key, IGrainFactory grains) =>
{
    await ChargerSimAttendee(grains, actionId, key).KillMyChargers();
    return Results.Ok(new { killed = true });
});

api.MapGet("/chargersim/{actionId}/attendee/{key}/charger/{chargerId}", async (string actionId, string key, string chargerId, IGrainFactory grains) =>
{
    var snapshot = await ChargerSimAttendee(grains, actionId, key).GetCharger(chargerId);
    return snapshot is null ? Results.NotFound() : Results.Ok(snapshot);
});

api.MapGet("/chargersim/{actionId}/attendee/{key}/charger/random/{state}", async (string actionId, string key, string state, IGrainFactory grains) =>
{
    var attendee = ChargerSimAttendee(grains, actionId, key);
    var snapshot = state.ToLowerInvariant() switch
    {
        "active" => await attendee.GetRandomActiveCharger(),
        "paused" => await attendee.GetRandomPausedCharger(),
        _ => null
    };
    return snapshot is null ? Results.NotFound() : Results.Ok(snapshot);
});

api.MapPost("/chargersim/{actionId}/attendee/{key}/charger/{chargerId}/{command}", async (string actionId, string key, string chargerId, string command, IGrainFactory grains) =>
{
    if (!Enum.TryParse<SingleChargerCommandType>(command, ignoreCase: true, out var cmd))
    {
        return Results.BadRequest(new { error = $"unknown command '{command}'" });
    }

    try
    {
        var snapshot = await ChargerSimAttendee(grains, actionId, key).CommandCharger(chargerId, cmd);
        return snapshot is null ? Results.NotFound() : Results.Ok(snapshot);
    }
    catch (InvalidOperationException ex) { return ChargerSimError(ex); }
});

// Unmatched /api/* routes return 404 (JSON) rather than falling through to the
// SPA fallback below and serving index.html.
api.MapFallback(() => Results.NotFound());

// SPA fallback: any other non-API route serves index.html so client-side
// routing and page refreshes work.
app.MapFallbackToFile("index.html");

app.Run();

// Request bodies for the minimal-API endpoints.
internal sealed record NameRequest(string Name);
internal sealed record CreateActionRequest(string Title, string[]? Options);
internal sealed record AnswerRequest(int OptionIndex);
internal sealed record ReactionRequest(string Kind);
internal sealed record TraceToggleRequest(bool Enabled);
internal sealed record CreateChargerSimRequest(string Title);
internal sealed record AmountRequest(int Amount);
internal sealed record KillSwitchRequest(bool Enabled);
internal sealed record BatchRequest(string Command, int Amount);
