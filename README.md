# Orleans + Svelte single-container starter

A minimal but production-minded starter that runs a **.NET 10 / Orleans 10**
backend and a **Svelte 5 / SvelteKit** static frontend as **one container**,
designed for **Azure Container Apps** with external HTTP ingress.

Everything runs in a single ASP.NET Core process:

- ASP.NET Core hosts the HTTP API (minimal APIs).
- The same process hosts an **Orleans silo** (co-hosted).
- The Svelte static build is served from `wwwroot`.

It is safe to expose publicly as a demo: the Orleans dashboard is opt-in and
off by default, no Swagger in production, no secrets in the frontend, and no
Orleans silo/gateway ports exposed externally (only HTTP/8080).

## Architecture

```
            ┌──────────────────────────────────────────────┐
 Browser ──▶│  Azure Container App (single container, :8080)│
            │                                               │
            │   ASP.NET Core                                │
            │     ├─ static files  ──▶ wwwroot (Svelte SPA) │
            │     ├─ /api/* minimal API endpoints           │
            │     └─ Orleans silo (in-process, internal)    │
            └──────────────────────────────────────────────┘
```

- **Svelte static frontend** — built with `@sveltejs/adapter-static`, output
  into `src/App.Api/wwwroot`, served as plain static files. It calls **relative**
  API URLs (`/api/counter/demo`), so it works behind any host/ingress.
- **ASP.NET Core API** — minimal API endpoints under `/api`, fixed-window rate
  limiting, Swagger in Development only, SPA fallback to `index.html`.
- **Orleans silo** — co-hosted in the API process. A `CounterGrain` keyed by a
  string id provides `Get` / `Increment` / `Reset`. Clustering and persistence
  are environment-driven (see below).
- **One Azure Container App initially** — start with a single replica using
  local Orleans clustering, then switch to Azure Storage before scaling out.

### API endpoints

| Method | Route                          | Description                  |
| ------ | ------------------------------ | ---------------------------- |
| GET    | `/api/health`                  | `{ "status": "ok" }`         |
| GET    | `/api/counter/{id}`            | Current counter value        |
| POST   | `/api/counter/{id}/increment`  | Increment and return value   |
| POST   | `/api/counter/{id}/reset`      | Reset to 0                   |

### ChargerSim presentation action

`ChargerSim` is a presenter-activated action that demonstrates Orleans at scale:
every attendee runs their own fleet of up to **5,000 simulated EV chargers**,
and **each charger is its own grain with its own 30-second Orleans reminder**
that drives a small randomized state machine (sessions start, charge, pause,
resume and stop; sessions last ~3 minutes on average).

Aggregation is **push-based and never polls the chargers**. After every tick and
every command a charger publishes its *absolute* current contribution
(`ChargerAggregateContribution`) to a per-attendee
`AttendeeChargerAggregateGrain`, which keeps a live `ChargerFleetSummary`
incrementally. Updates carry a monotonic version, so the aggregate is idempotent
and safe against duplicate or out-of-order updates. The presenter dashboard rolls
the per-attendee summaries up into a global view by reading the aggregate grains
(one per attendee) — again, never the individual chargers.

Charger **creation and batch commands are processed asynchronously in the
background**. An attendee request doesn't fan out inline — it records intent on
the `IAttendeeChargerSimGrain`: creation bumps a *pending charger count* and a
command is appended to a *queue*. A grain timer (the background worker) then
reconciles the fleet toward that intent, creating chargers in chunks and
executing one queued command per tick, until both the pending count and the queue
reach zero (at which point the worker stops so the grain can deactivate). This
keeps every request snappy regardless of fleet size; the attendee UI polls
`GetWorkStatus` to show how much work is still outstanding. Single-charger
commands and fleet reads stay synchronous. The cap of 5,000 live chargers is
enforced when a request is accepted (reserving room for work already queued), so
rapid requests can never overshoot it.

The `IChargerSimActionGrain` is a single, hot grain, so reads and writes are kept
**off its critical path**:

- **Dashboard is served from a cached snapshot.** Instead of fanning out to every
  attendee + aggregate grain on each presenter poll (`2N+1` grain calls), the action
  grain refreshes a `ChargerSimDashboard` snapshot on a ~1s background timer and
  serves polls from that cache (the live event ticker is overlaid at read time). The
  timer stops when no presenter has polled for a while, so the grain can deactivate.
- **Kill-switch and active state are pushed, not pulled.** The per-contribution
  hot path used to call `IsKillSwitchEnabled()` on the action grain for every charger
  tick; now the action grain *pushes* the flag to each `AttendeeChargerAggregateGrain`
  on toggle (and each aggregate pulls it once on activation), so `UpsertContribution`
  reads a local cache. Likewise `Activate()`/`Deactivate()` push the active flag to
  the attendee controller grains, so their `EnsureActive()` gate no longer calls back
  into the action grain on every command. `IsActive()`/`IsKillSwitchEnabled()` are
  `[AlwaysInterleave]` so those activation-time pulls can't deadlock against the
  dashboard fan-out that triggers them.

The presenter dashboard also **loads the latest snapshot immediately on mount**, so a
page reload shows fresh data right away, and the kill-switch toggle is **optimistic**
(it flips instantly and reconciles on the next poll).

Grain key conventions:

```
action-{actionId}                                    IChargerSimActionGrain
action-{actionId}/attendee-{attendeeId}              IAttendeeChargerSimGrain
action-{actionId}/attendee-{attendeeId}/aggregate    IAttendeeChargerAggregateGrain
action-{actionId}/attendee-{attendeeId}/charger-{n}  IChargerGrain   (display id "CP-000042")
```

Because chargers use a 30-second reminder period (below Orleans' one-minute
default minimum), the silo sets `ReminderOptions.MinimumReminderPeriod` to 15s,
and the host enables a reminder service: in-memory in `Local` mode and Azure
Table reminders in `AzureStorage` mode.

### Orleans clustering / persistence modes

Selected by the `ORLEANS_CLUSTERING` environment variable. Application/grain
code is identical in both modes — only the host configuration changes.

| `ORLEANS_CLUSTERING` | Clustering            | Grain storage (`counterStore`) | Use for                          |
| -------------------- | --------------------- | ------------------------------ | -------------------------------- |
| `Local` (default)    | `UseLocalhostClustering` | In-memory                   | Local dev / single-replica demo  |
| `AzureStorage`       | Azure Table clustering   | Azure Table storage         | Production / multiple replicas   |

`AzureStorage` mode reads `ORLEANS_AZURE_STORAGE_CONNECTION_STRING` from the
environment/config only. No secrets are hardcoded; the app fails fast at startup
if the mode is `AzureStorage` but the connection string is missing.

### Orleans dashboard (optional)

The official Orleans dashboard (`Microsoft.Orleans.Dashboard`, preview) is wired
into the co-hosted silo but **disabled by default**. Set `ORLEANS_DASHBOARD=true`
to enable it; it is then served at **`/dashboard`** in the same process:

```bash
ORLEANS_DASHBOARD=true dotnet run --project src/App.Api
# then open http://localhost:8080/dashboard
```

It shows silos, grain activations, per-method profiling, reminders and live
logs. It has **no built-in authentication**, so only enable it behind a trusted
network — never on a publicly reachable deployment. Leaving the variable unset
maps no dashboard routes at all.

## Prerequisites (local)

- [.NET 10 SDK](https://dotnet.microsoft.com/download)
- [Node.js LTS](https://nodejs.org/)
- [Docker](https://www.docker.com/)

## Local development

Build the frontend (emits into `src/App.Api/wwwroot`), then run the backend:

```bash
# 1. Install frontend dependencies
cd frontend
npm install

# 2. Build the static frontend into src/App.Api/wwwroot
npm run build

# 3. Run the backend (serves the API + the built frontend)
cd ../src/App.Api
dotnet run
```

Then open the printed URL (e.g. `http://localhost:5000`). Check the API:

```bash
curl http://localhost:5000/api/health
curl -X POST http://localhost:5000/api/counter/demo/increment
curl http://localhost:5000/api/counter/demo
```

> Frontend-only iteration: run `npm run dev` in `frontend/` (with the backend
> also running). `vite dev` proxies `/api` to `http://localhost:5000`.

### Build and run the Docker image locally

```bash
# Build the image (frontend + backend, multi-stage)
docker build -t orleans-demo .

# Run it; only port 8080 is exposed
docker run --rm -p 8080:8080 orleans-demo
```

Open `http://localhost:8080`.

## Running with Azure Storage

By default the app uses `ORLEANS_CLUSTERING=Local` (in-memory storage), which is
perfect for `dotnet run` and a single-replica demo but **does not persist** —
the counter resets when the process restarts. To use durable Azure Table storage
(and the clustering required before scaling to multiple replicas), set
`ORLEANS_CLUSTERING=AzureStorage` and provide a connection string.

Orleans creates the tables it needs automatically on first run:

| Table                  | Purpose                              |
| ---------------------- | ------------------------------------ |
| `OrleansSiloInstances` | Cluster membership (clustering)      |
| `OrleansGrainState`    | Grain state (counter persistence)    |

### Option A — locally with the Azurite emulator (no Azure account)

The simplest way to run the **exact** Azure Storage code path locally is the
[Azurite](https://learn.microsoft.com/azure/storage/common/storage-use-azurite)
emulator. A `docker-compose.yml` is included that starts Azurite and the app
together in `AzureStorage` mode:

```bash
docker compose up --build
# open http://localhost:8080
```

Increment the counter, then restart (`docker compose restart app`) — the value
**persists**, unlike in `Local` mode.

> Prefer not to use Docker for Azurite? Install it via npm
> (`npm install -g azurite`), run `azurite`, then start the app with
> `ORLEANS_CLUSTERING=AzureStorage` and
> `ORLEANS_AZURE_STORAGE_CONNECTION_STRING="UseDevelopmentStorage=true"`.

### Option B — a real Azure Storage account

1. Create a **Storage account** (general-purpose v2 is fine).
2. Copy a **connection string** from *Access keys* in the portal (or
   `az storage account show-connection-string`).
3. Run the app with:

   ```bash
   ORLEANS_CLUSTERING=AzureStorage \
   ORLEANS_AZURE_STORAGE_CONNECTION_STRING="<your connection string>" \
   dotnet run            # or pass these as env vars to the container
   ```

No tables need to be created by hand, and no firewall changes are required
beyond allowing your app to reach the storage account. Treat the connection
string as a secret — keep it in environment variables / a secret store, never
in the frontend or source. (A future hardening step is to switch to managed
identity instead of a connection string.)

## Manual Azure Container Apps deployment

No infrastructure-as-code is included — deploy manually.

1. **Build and push the image** to a registry (e.g. Azure Container Registry):

   ```bash
   docker build -t <registry>/orleans-demo:latest .
   docker push <registry>/orleans-demo:latest
   ```

2. **Create a Container App** with:
   - **External HTTP ingress** enabled.
   - **Target port: `8080`**.
   - **Min replicas: `1`** to start.

3. **Environment variables:**

   For the first single-replica demo:

   ```
   ASPNETCORE_ENVIRONMENT=Production
   ORLEANS_CLUSTERING=Local
   ```

   To use Azure Storage clustering/persistence (required before scaling out):

   ```
   ASPNETCORE_ENVIRONMENT=Production
   ORLEANS_CLUSTERING=AzureStorage
   ORLEANS_AZURE_STORAGE_CONNECTION_STRING=<your connection string>
   ```

   Store the connection string as a Container App secret, not in plain config.

**Recommended first deploy:** start with one replica and `ORLEANS_CLUSTERING=Local`.
Once the app is reachable, switch to `AzureStorage` clustering/persistence
**before** testing multiple replicas.

## Safety notes

- **Local clustering is single-instance only.** It is suitable for local dev or
  a single-replica demo. If you scale to multiple replicas, switch to Azure
  Storage (or another real Orleans clustering provider) **first** — otherwise
  replicas won't form a cluster and behavior will be inconsistent.
- **Do not expose the Orleans dashboard publicly.** The official Orleans
  dashboard (preview) is wired up but **off by default**. Enable it only behind
  a trusted network by setting `ORLEANS_DASHBOARD=true`; it then serves at
  `/dashboard` and exposes cluster internals (silos, grain activations, method
  profiling, reminders, live logs) with no built-in authentication.
- **Do not put secrets in the Svelte app.** The frontend is static and fully
  public; it only calls relative `/api` URLs.
- **Authentication is not included.** Add authentication/authorization before
  storing or serving user-specific data.

## Troubleshooting

| Symptom                              | Likely cause / fix                                                      |
| ------------------------------------ | ----------------------------------------------------------------------- |
| Blank frontend                       | Frontend not built — run `npm run build` in `frontend/`.                |
| 404 when refreshing a client route   | SPA fallback missing — ensure `app.MapFallbackToFile("index.html")` and that `wwwroot/index.html` exists. |
| Container App not reachable          | Check ingress **target port is 8080** and ingress is external.          |
| Counter resets unexpectedly          | Local/in-memory storage in use — switch to `ORLEANS_CLUSTERING=AzureStorage`. |
| Multiple replicas behaving oddly     | Using local clustering across replicas — use real Orleans clustering/persistence (Azure Storage). |

## Project structure

```
/
  Dockerfile                  Multi-stage build (Node frontend → .NET publish → aspnet runtime)
  src/App.Api/                ASP.NET Core API + co-hosted Orleans silo
    Program.cs                Host wiring, endpoints, rate limiting, static + SPA fallback
    Grains/CounterGrain.cs    Counter grain implementation + persisted state
    GrainContracts/ICounterGrain.cs
    wwwroot/                  Generated by the SvelteKit build (gitignored)
  frontend/                   SvelteKit static frontend (adapter-static)
    src/routes/+page.svelte   Counter UI
```
