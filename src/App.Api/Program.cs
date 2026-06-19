using System.Text;
using System.Threading.RateLimiting;
using App.Api.GrainContracts;
using App.Api.Observability;
using Microsoft.AspNetCore.RateLimiting;

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
    }
    else
    {
        // Local development / single-replica demo only.
        silo.UseLocalhostClustering();
        silo.AddMemoryGrainStorage("store");
    }

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

// --- Cluster activity (presenter-only, debug/demo visualization) -------------

// Snapshot of every active activation and the silo it lives on.
api.MapGet("/cluster/activations", async (HttpRequest req, IGrainFactory grains) =>
{
    if (!PresenterOk(req)) return Results.Unauthorized();

    var inventory = grains.GetGrain<IActivationInventoryGrain>(0);
    // Idempotent: ensures the polling timer is running before we read.
    await inventory.Start();
    return Results.Ok(await inventory.GetSnapshot());
});

// The most recent grain-to-grain calls observed across the cluster.
api.MapGet("/cluster/calls", async (HttpRequest req, IGrainFactory grains) =>
{
    if (!PresenterOk(req)) return Results.Unauthorized();

    var recorder = grains.GetGrain<IClusterCallRecorderGrain>(0);
    return Results.Ok(await recorder.GetRecent());
});

// Cluster-wide call-tracing toggle. Disabling it makes the filter stop recording
// (a local volatile read per call) and the reporter stop flushing; the change
// propagates to every silo within the ~100ms poll interval.
api.MapGet("/cluster/tracing", async (HttpRequest req, IGrainFactory grains) =>
{
    if (!PresenterOk(req)) return Results.Unauthorized();

    var state = await grains.GetGrain<IClusterTraceControlGrain>(0).GetState();
    return Results.Ok(new { enabled = state.Enabled });
});

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
internal sealed record TraceToggleRequest(bool Enabled);
