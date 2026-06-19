using System.Threading.RateLimiting;
using App.Api.GrainContracts;
using Microsoft.AspNetCore.RateLimiting;

var builder = WebApplication.CreateBuilder(args);

// ---------------------------------------------------------------------------
// Orleans silo, co-hosted in this same ASP.NET Core process.
//
// Clustering/persistence are selected by ORLEANS_CLUSTERING:
//   Local        -> localhost clustering + in-memory grain storage (dev / single replica)
//   AzureStorage -> Azure Table clustering + Azure Table grain persistence
//
// The grain storage provider is always named "counterStore" so grain code
// (CounterGrain) is identical regardless of the backend.
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

        silo.AddAzureTableGrainStorage("counterStore", options =>
            options.TableServiceClient = new Azure.Data.Tables.TableServiceClient(connectionString));
    }
    else
    {
        // Local development / single-replica demo only.
        silo.UseLocalhostClustering();
        silo.AddMemoryGrainStorage("counterStore");
    }
});

// ---------------------------------------------------------------------------
// Web services.
// ---------------------------------------------------------------------------

// Basic fixed-window rate limiting for public safety. Applied to /api routes.
builder.Services.AddRateLimiter(options =>
{
    options.RejectionStatusCode = StatusCodes.Status429TooManyRequests;
    options.AddFixedWindowLimiter("api", limiter =>
    {
        limiter.PermitLimit = 100;
        limiter.Window = TimeSpan.FromMinutes(1);
        limiter.QueueLimit = 0;
        limiter.QueueProcessingOrder = QueueProcessingOrder.OldestFirst;
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

api.MapGet("/counter/{id}", async (string id, IGrainFactory grains) =>
{
    var value = await grains.GetGrain<ICounterGrain>(id).Get();
    return Results.Ok(new { id, value });
});

api.MapPost("/counter/{id}/increment", async (string id, IGrainFactory grains) =>
{
    var value = await grains.GetGrain<ICounterGrain>(id).Increment();
    return Results.Ok(new { id, value });
});

api.MapPost("/counter/{id}/reset", async (string id, IGrainFactory grains) =>
{
    await grains.GetGrain<ICounterGrain>(id).Reset();
    return Results.Ok(new { id, value = 0 });
});

// Unmatched /api/* routes return 404 (JSON) rather than falling through to the
// SPA fallback below and serving index.html.
api.MapFallback(() => Results.NotFound());

// SPA fallback: any other non-API route serves index.html so client-side
// routing and page refreshes work.
app.MapFallbackToFile("index.html");

app.Run();
