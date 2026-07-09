# DropHarvester — Containerization Plan (headless Docker service)

Status: **IMPLEMENTED** (2026-07-06). This is the original design; the shipped code lives in
`DropHarvester.Core/`, `DropHarvester.Daemon/`, `Dockerfile`, `docker-compose.yml` and `.env.example`.
See the "Run in Docker" section of the README for usage.

## The core reality

DropHarvester is a .NET MAUI **GUI** app (WinUI + Mac Catalyst). MAUI cannot run
headless in a Linux container — there is no display, and WinUI/Mac Catalyst do not
run on Linux at all. So "containerize DropHarvester" really means:

> Extract the harvesting **engine** into a headless service that shares the exact same
> core code, and add a Docker entrypoint as a second front-end alongside the desktop app.

Good news from surveying the code: the engine is only **shallowly** coupled to MAUI.
The entire dependency surface in the core is two things:

- `FileSystem.AppDataDirectory` — 2 sites (`Services/JsonStore.cs`, `Services/StatsService.cs`).
- `MainThread.BeginInvokeOnMainThread` / `InvokeOnMainThreadAsync` — ~9 sites
  (`HarvesterOrchestrator`, `ChannelManager`, `InventoryService`, `AlertsCoordinator`).

No `Preferences`, no `SecureStorage`, no deep entanglement. The HTTP + websocket +
JSON engine is genuinely portable. The one *new* behavior to design is headless auth.

## Target project layout

Today it's a single MAUI project (`SingleProject=true`). Move to a solution:

```
DropHarvester.sln
├── DropHarvester.Core/     net10.0, NO MAUI  — the engine (shared)
├── DropHarvester/          existing MAUI app  — references Core (desktop front-end)
└── DropHarvester.Daemon/   net10.0 worker     — references Core (container front-end)
```

**Hard rule for the whole effort: the desktop app must keep building and behaving
identically at every step.** It is the primary product; the daemon is additive.

## Phase 1 — Decouple from MAUI (in place, no behavior change)

1. **`IUiDispatcher`** abstraction (in Core):
   - `void Post(Action)` + `Task InvokeAsync(Action)`.
   - MAUI impl → `MainThread.BeginInvokeOnMainThread` / `InvokeOnMainThreadAsync`.
   - Headless impl (`InlineDispatcher`) → runs inline (no UI thread to marshal to).
   - Inject it into the ~9 core sites that currently call `MainThread.*` and replace them.
2. **Data-dir abstraction**:
   - `IAppPaths { string DataDir }` (or a resolved config value).
   - MAUI → `FileSystem.AppDataDirectory`. Headless → env `DROPHARVESTER_DATA` (default `/data`).
   - Replace the 2 `FileSystem.AppDataDirectory` sites.
3. Rebuild + smoke-test the desktop app: identical behavior.

## Phase 2 — Extract `DropHarvester.Core`

- New class library, `net10.0`, no `UseMaui`, references `CommunityToolkit.Mvvm`
  (cross-platform — models keep their `[ObservableProperty]`).
- **Move to Core:** `Models/` (Twitch, Auth, Events, Settings), `Services/Twitch/*`
  (auth, GQL/http builder, inventory, watch, websocket pool, channel manager,
  orchestrator), `JsonStore`, settings/state stores, the `IHarvesterEventBus`,
  `WebhookNotifier`, `StatsService`/history, and the **version-check** part of `UpdateService`.
- **Keep in the MAUI app:** `Views/`, `ViewModels/`, `App`/`AppShell`, `Platforms/`,
  `UiObservableCollection`, tray/notification/autostart, NAudio sound, and the
  **installer-launching** part of `UpdateService` (containers update via image pull).
- Add `AddDropHarvesterCore(IServiceCollection)` in Core; both `MauiProgram` and the
  daemon host call it, so DI wiring lives in one place.
- **Trickiest bit:** confirm the Twitch models' base (`ObservableModel`) and
  `UiObservableCollection` don't force a UI thread. If a model's change-notification
  or collection mutation needs the dispatcher, route it through `IUiDispatcher` so the
  headless build uses `InlineDispatcher`. This is the one spot the extraction could get
  fiddly; everything else is a file move + namespace fixups.

## Phase 3 — `DropHarvester.Daemon` (headless worker)

- .NET Generic Host (`Microsoft.Extensions.Hosting`) + a `BackgroundService` that:
  - Reads config from `appsettings.json` + env vars (priority games, exclude/dedupe
    lists, webhook URL + per-event toggles, data dir, log level).
  - News up the orchestrator via `AddDropHarvesterCore`, `InlineDispatcher` for the dispatcher.
  - Subscribes to `HarvesterEvent`s → structured **stdout logging** (container-native) +
    the existing webhook notifier (ideal for a background service).
  - Handles SIGTERM via host lifetime so `docker stop` is graceful.
- **Auth (the one real UX wrinkle):** Twitch device-code flow needs a browser once.
  Headless, the daemon prints the `twitch.tv/activate` URL + user code to the logs,
  polls until authorized, and persists the token to `/data` so restarts don't re-auth.
  Token expiry re-prompts in the logs. (This is the standard pattern for headless
  drop harvesters.)
- **Optional:** a tiny HTTP `/healthz` + `/status` endpoint for `docker healthcheck`
  and at-a-glance state. Can ship v1 with logs only and add this later.

### Config surface (env vars, illustrative)

```
DROPHARVESTER_DATA=/data
DH_PRIORITY_GAMES=Game A,Game B
DH_EXCLUDE_GAMES=...
DH_DEDUPE_GAMES=...
DH_WEBHOOK_URL=https://discord.com/api/webhooks/...
DH_WEBHOOK_EVENTS=drop-claimed,campaign-complete,login-expired
DH_LOG_LEVEL=Information
```

## Phase 4 — Docker packaging

- **Multi-stage Dockerfile:**
  - Build: `mcr.microsoft.com/dotnet/sdk:10.0` →
    `dotnet publish DropHarvester.Daemon -c Release -r linux-x64 --no-self-contained`.
  - Runtime: `mcr.microsoft.com/dotnet/runtime:10.0`, non-root user, `VOLUME /data`,
    `ENTRYPOINT ["dotnet","DropHarvester.Daemon.dll"]`.
  - Build **linux-arm64** too (people run these on Pis / ARM NAS) — multi-arch.
- `docker-compose.yml`: `restart: unless-stopped`, `volumes: ./data:/data`,
  `env_file: .env`, healthcheck (if the /healthz endpoint lands).
- `.dockerignore`; README section: run instructions + the one-time device-code auth
  (`docker logs`) + env config reference.

## Verification

1. Run the daemon **locally first** (`dotnet run` on Windows — the headless path is
   cross-platform): device-code auth, confirm a drop advances. Cheapest way to prove
   the extraction before Docker enters the picture.
2. `docker build` + `docker run` with `./data` mounted: auth via logs, confirm harvesting.
3. Regression: desktop MAUI app still builds on Windows + Mac and behaves identically.

## Risks / open questions

- **Models base class** (`ObservableModel` / `UiObservableCollection`) is the one place
  the extraction could get sticky — see Phase 2. Everything else is mechanical.
- **Auth UX** is the only genuinely new behavior; the log-the-code approach is proven.
- **Proxy:** `HttpClientBuilder` sets `HttpClientHandler.Proxy` (flagged unsupported on
  maccatalyst); on Linux it's fully supported — just verify.
- **Distribution: DECIDED — ship only the Dockerfile.** Users build their own image; no
  prebuilt GHCR/Docker Hub image to publish or maintain.
- **Open:** does v1 include the `/healthz` + `/status` HTTP endpoint, or ship logs-only first?
- **In-container updates** are by image pull — the in-app auto-updater doesn't apply here
  (and its installer-launch code stays in the MAUI app, not Core).
