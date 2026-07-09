# DropHarvester

A native **.NET 10 MAUI** desktop app for **Windows and macOS** that harvests Twitch
drops **without downloading the stream** - it emulates "watching" by sending a
minute-watched heartbeat, so it earns drops on near-zero bandwidth. Modern UI with
a number of extra features.

> **Note:** this automates Twitch's private API, which is against Twitch's Terms
> of Service. It's a tool for personal use - use it at your own discretion.

## Screenshots

| Status | Settings |
| :---: | :---: |
| ![Status tab](docs/screenshots/1.webp) | ![Settings tab](docs/screenshots/2.webp) |

## Features

### Core harvesting

- **Stream-less drop harvesting** - no video download; ~59s minute-watched heartbeat.
- **Automatic campaign discovery** from your linked accounts.
- **Drops-enabled validation** - only harvests channels that can actually earn the drop.
- **Auto-claim** drops the instant they complete (reacting to the claim websocket
  event), with a safety net that re-scans every campaign at startup and once a
  minute and retries forever - so a completed drop is never left unclaimed, even
  through a missed websocket message, a connection failure, or an app restart.
- **auto start/stop** as campaigns appear and finish.
- **Sharded PubSub websockets** - track many channels for real-time online/offline
  and drop events.
- **Channel-points auto-claim** on the watched channel (optional).
- **Persistent login** - device-code OAuth; the token is saved and reused.

### What to harvest, and in what order

- **Game priority & exclusion lists** - harvest what you want, in the order you want.
- **Prioritize ending soonest** - when enabled, campaigns are harvested by soonest
  expiry, but the **priority list is still honored** wherever there's slack: a
  priority drop is harvested ahead of a sooner one whenever deferring it wouldn't lose
  it, and a sooner non-priority drop is only chosen when it won't cost you a
  priority drop. (Earliest-deadline-first with priority promotion, so nothing
  earnable is lost.)
- **Prioritize by availability** (opt-in) - a tiebreaker on top of the order
  above: when campaigns are otherwise tied, the game with **fewer** live
  drops-enabled streamers is harvested first, so scarce-stream drops are grabbed while
  a channel is actually live and plentiful games wait. Never overrides your
  priority list.
- **Priority-only mode** - ignore everything not on the priority list.
- **Harvest unlinked games** (opt-in per game) - attempt games your account isn't
  linked to; always harvested at the lowest priority.
- **Per-game de-duplication** - add a game to the de-dupe list and it skips any
  drop whose reward you already own (from Twitch's claimed-drop history).

### Channels

- **Automatic channel switching** - reacts the instant the watched stream goes
  down (via the stream-state websocket) and immediately re-scans every game's live
  streams for the next target, rather than waiting for the next watch tick. If a
  manual override's only stream goes offline, it harvests the next-best campaign
  meanwhile and snaps back the moment the override target is live again - so it
  never just sits idle while there are drops it could be earning.
- **Official Campaign Channel handling** - channel-specific drops are watched on
  their official channel first (for time efficiency), while generic drops keep
  progressing on any channel.
- **Prefer / Avoid channels** - right-click a channel in the **Channels** tab:
  - **⭐ Prefer** - when a preferred channel is live for the game being harvested and no
    official channel is required, DropHarvester idles on it instead of a random
    top-viewer stream (the drop still credits, so you support the streamer at no
    cost to harvesting). Priority/ending-soonest still decides *which game* is harvested;
    an official channel always wins.
  - **🚫 Avoid** - never idled on unless it's the only drops-enabled stream live for
    that game right now.
  - Both lists are also shown and editable (type a channel login, or remove) under
    **Settings**.
- **Collapsible per-game groups** - the Channels tab groups streams under each game
  (in harvesting order), with an Official Campaign Channel column, live counts, and a
  spinner while the list refreshes. The list fills in progressively and updates in
  the background about every 5 minutes.
- **"Not crediting" fast-switch** - if a drop makes no progress on an open-campaign
  channel for a few minutes, that channel is benched and another is tried (official
  channels are never fast-switched, since only they can credit their drop).

### Inventory & status

- **Live inventory** - the Campaigns tab shows every campaign with its drops and
  progress, updates progress live while harvesting, and puts a purple **Harvesting** badge
  on the campaign being harvested right now.
- **Claim-based campaign tabs** - each campaign sits in exactly one tab:
  **Finished** = every reward actually *claimed* (a drop watched to 100% but not yet
  claimed is still actionable and stays under Upcoming, never Finished); **Expired**
  = past its end date, claimed or not; **Upcoming** = anything else - not started
  yet, or still has an unclaimed reward. Claim attribution uses reward-id
  normalization and claim-time matching so re-used reward ids across concurrent
  campaigns don't wrongly mark a still-earnable campaign done. Recurring or
  region-variant campaigns that share a game + name collapse to a single row.
- **Language** - a Settings dropdown switches the UI language live (no restart).
  Supported: English, Español, Français, Deutsch, Русский, 简体中文, 日本語, 한국어,
  Nederlands. All diagnostic logs are in English.

### Alerts & feedback

- **Desktop notifications** - drop claimed, campaign complete, all drops harvested,
  login expired.
- **Drop-claimed sound** - choose any sound file (wav/mp3/m4a/aac/wma/aiff/ogg),
  the **output device** (Windows can target any device; macOS uses the system
  default), and the **volume**; a Test button plays it.
- **Remote webhook alerts** - Discord / Slack / generic webhook, with per-event
  toggles (new drop available, drop claimed, campaign complete, all harvested,
  login expired).
- **Connection watchdog** - if Twitch Drops go down or the connection is lost for a
  sustained stretch (~20 min), a banner lets you know harvesting is paused; it resumes
  automatically once the connection is back.

### Stats, log & app

- **Stats & history dashboard** - lifetime drops/campaigns/watch-time, a 7-day
  claims chart (hover a bar to see what was claimed that day), recent history, and
  CSV/JSON export.
- **Log tab** - the running output, with a **Copy** button (copies the whole
  visible log to the clipboard) and auto-scroll that follows the newest line.
- **Debug server** - an optional local HTTP endpoint that exposes everything the
  app is doing under the hood. See [Debug server](#debug-server) below.
- **Tray / menu-bar mode** - on Windows, closing the window hides it to the system
  tray and harvesting keeps running; the tray icon restores it, right-click for Open /
  Quit. (macOS keeps running when the window is closed; a menu-bar item is planned.)
- **Resilience** - all Twitch calls retry transient failures (network/timeout/5xx/
  429) with backoff, and claim/sync/points errors are logged and retried rather
  than dropping the channel or killing the loop.
- **In-app updates** - a real installer (Inno Setup `.exe` on Windows, `.pkg` on
  macOS). The app silently checks a per-OS manifest on startup and pre-downloads
  any newer version in the background. When one is ready, the Status tab shows an
  **Update now** button
  (silent install + relaunch); if you don't, it **auto-installs on the next
  launch**. Settings has a manual **Check now** button. No download step, no
  yes/no prompt - updates just happen.
- **Autostart** with the OS (Windows Run key / macOS LaunchAgent), optionally
  straight into the tray.

## Debug server

The debug server is a small **localhost-only** HTTP endpoint for inspecting exactly
what the app is doing - which Twitch calls ran, what Twitch reported, and every
harvest / skip / claim decision the app made from that data. It's the fastest way to
answer "why is *this* being harvested / skipped / shown as claimed?".

**Enable it:** Settings -> **DEBUG SERVER** -> toggle **Run local debug server**
(default port **5757**, editable). It starts immediately and again on app launch
while the toggle is on. It binds to `127.0.0.1` only (no external access) and uses
a raw socket, so it needs no admin rights or firewall changes.

Open **`http://localhost:5757/`** in a browser. Endpoints:

- **`/`** - index with links to the endpoints below.
- **`/snapshot`** - the main one: a JSON snapshot of live state and every decision.
- **`/log`** - the rolling log as plain text (up to 2000 recent lines), the same
  content as the Log tab.
- **`/crashlog`** - the app's `crash.log` (unhandled exceptions plus caught
  UI-dispatch errors), for diagnosing a crash without hunting the file on disk.

### What `/snapshot` contains

Top level:

- `GeneratedUtc`, `IsRunning`
- `Summary` - the current status line, including the reason when idle (e.g. "waiting
  for a stream to come online") so you can tell *why* nothing is being harvested
- `Active` - the channel / game / campaign / drop being watched right now
- `Override` - the manual "Harvest" override state: whether one is `Active`, its
  `CampaignId` / `Campaign` name, and whether it's `DropOnly`
- `LastClaimSweepUtc` - when the all-campaign claim safety-net last ran
- `Settings` - the harvesting-relevant settings (ending-soonest, availability-priority,
  priority-only, impossible drops, badges/emotes, and the priority / excluded /
  de-dupe / harvest-unlinked lists)
- `ClaimHistoryCount` and `ClaimHistory` - the full claimed-reward history as
  `RewardId -> AwardedAt` (normalized reward ids, the same keys used for matching)
- `SkippedDrops` - how many drops were given up on this session
- `BenchedChannels` - channels temporarily benched for not crediting, with the
  time each is benched until
- `HarvestableGames` - the harvestable games in harvesting order
- `LiveStreamersByGame` - live drops-enabled streamer count per game from the last
  channel refresh (the input to the availability tiebreaker)

`Campaigns` - the candidate campaigns in harvesting order, each with **why** it is or
isn't being harvested:

- `Id`, `Name`, `Game`, `StartsAt`, `EndsAt`, `Linked`, `AllowedChannels`
- `LiveStreamers` - live drops-enabled streamer count for this campaign's game
- `Eligible` - passes the link/badge eligibility check
- `BlockReason` - `null` if harvestable now, otherwise the human reason it's passed
  over (not linked, already claimed this campaign, reward already owned, no time
  left, given up, etc.)
- `FinishedForHarvesting` - every drop is claimed/complete
- `Drops[]` - each drop with:
  - `Name`, `RequiredMinutes`, `CurrentMinutes`, `IsClaimed`, `IsComplete`
  - `ClaimedThisCampaign` - the claim-history verdict for this campaign
  - `GivenUp` - given up on this session (no progress)
  - `Benefits[]` - each reward with `Id`, `MatchKey` (the normalized id used for
    matching), `Name`, `ClaimedAt` (award time from the claim history, or `null`),
    `GrantedByCampaigns` (how many active campaigns grant this reward), and
    `AttributedHere` (whether a claim of this reward is attributed to *this*
    campaign).

### What you can diagnose with it

- **Why a game/drop is being skipped** - read `BlockReason` and the per-drop flags
  for that campaign.
- **Whether a drop is really claimed** - check `IsClaimed` / `IsComplete` (Twitch's
  state) and `ClaimedThisCampaign` / the benefit's `ClaimedAt` + `AttributedHere`
  (the claim-history match). If `ClaimedAt` is `null`, the reward isn't in your
  claim history under that id.
- **Claim-attribution across concurrent campaigns** - `GrantedByCampaigns` and
  `AttributedHere` show when a reward is shared (ambiguous) versus uniquely tied to
  one campaign's window.
- **Channel behavior** - `Active` shows what's being watched; `BenchedChannels`
  shows what's been temporarily avoided for not crediting.
- **What Twitch actually reported** - `ClaimHistory` and each drop's minutes /
  claimed flags are the values read straight from the inventory response.

## Requirements

- .NET 10 SDK with the MAUI workload
  (`maui-windows` on Windows, `maccatalyst` on macOS).
- Windows 10 1809+ or macOS 15+.

## Build & run

The project targets each desktop OS on its native build host, so `dotnet` picks
the right framework automatically.

```bash
# Windows
dotnet build -f net10.0-windows10.0.19041.0 -c Debug
dotnet run   -f net10.0-windows10.0.19041.0

# macOS (requires Xcode)
dotnet build -f net10.0-maccatalyst -c Debug
dotnet run   -f net10.0-maccatalyst
```

## Usage

1. Launch the app and open the **Status** tab.
2. Click **Log in with Twitch** - a browser opens to `twitch.tv/activate`; enter
   the shown code and approve. (You must have linked your game accounts on Twitch
   yourself; the app only discovers campaigns for already-linked accounts.)
3. Harvesting starts automatically. Use **Settings** to set priority/excluded games,
   the de-dupe and harvest-unlinked lists, the drop-claimed sound, notifications,
   webhooks, tray, autostart, and the debug server.
4. Watch progress on **Status** and **Campaigns**; tracked streams on **Channels**
   (right-click to prefer/avoid); totals on **Stats**; and the running output on
   **Log**. Turn on the debug server if you want to inspect decisions in detail.

## Run in Docker (headless)

The same harvesting engine runs as a headless background service - no desktop, no UI -
for people who'd rather run DropHarvester on a server / NAS / Raspberry Pi. It shares
`DropHarvester.Core` with the desktop app; only the front-end differs.

```bash
# Build + start in the background
docker compose up -d --build

# First run only: watch the logs for the Twitch login prompt
docker compose logs -f
#   ==================== TWITCH LOGIN REQUIRED ====================
#     1) On any device, open:  https://www.twitch.tv/activate?device-code=XXXXXXXX
#     2) Enter this code:      XXXXXXXX
# Approve it once; the token is saved to the volume and reused on every restart.

# Live state / liveness
curl http://localhost:8080/status
curl http://localhost:8080/healthz     # -> "ok" (used by the container HEALTHCHECK)
```

Or without compose:

```bash
docker build -t dropharvester-daemon .
docker run -d --name dropharvester \
  -e DH_PRIORITY_GAMES="Rust,Fortnite" \
  -e DH_WEBHOOK_URL="https://discord.com/api/webhooks/..." \
  -v dropharvester-data:/data -p 8080:8080 \
  dropharvester-daemon
docker logs -f dropharvester          # first-run login code
```

Notes:

- **Auth is one-time.** The device-code login only happens on first run (or after the
  token is revoked); the token persists in the `/data` volume. If it ever expires, the
  daemon logs a fresh code and pauses harvesting until you approve it.
- **You still link game accounts on Twitch yourself** - the harvester only discovers
  campaigns for already-linked accounts.
- **Config is env-vars.** See [`.env.example`](.env.example) for the full list
  (`DH_PRIORITY_GAMES`, `DH_EXCLUDE_GAMES`, `DH_WEBHOOK_URL`, `DH_WEBHOOK_EVENTS`,
  `DH_CLAIM_CHANNEL_POINTS`, `DH_PROXY`, `DH_HEALTH_PORT`, `DH_LOG_LEVEL`, ...). Values
  set in the environment win over `settings.json` on the volume.
- **Webhooks come along** (Discord/Slack/generic) - ideal for a background service - but
  tray, desktop notifications, sound and the in-app auto-updater are desktop-only.
  Update a container by pulling/building a newer image.
- **Debug server (opt-in).** Set `DH_DEBUG_SERVER=true` (and map port `5757`) to expose the same
  rich endpoints the desktop app has - `/snapshot` (per-campaign/drop harvesting decisions + claim
  attribution), `/log` (rolling log), `/claims-raw` (raw Twitch claim history). Separate from the
  always-on `/healthz` + `/status`.
- **Multi-arch.** The image builds for `linux/amd64` and `linux/arm64`
  (`docker buildx build --platform linux/amd64,linux/arm64 ...`).
- **Bind mounts:** the container runs as non-root (uid 10001). A named volume (the
  compose default) just works; if you bind-mount a host directory instead, make it
  writable by that uid.

## Architecture

The solution is three projects:

- **`DropHarvester.Core`** - the MAUI-free harvesting engine, domain models and
  persistence, targeting plain `net10.0` so it runs anywhere .NET does (including a
  Linux container). Depends only on `CommunityToolkit.Mvvm`.
- **`DropHarvester`** - the desktop MAUI app (Windows + macOS); references Core and
  adds the UI, tray/notifications/sound, and the installer-based auto-updater.
- **`DropHarvester.Daemon`** - the headless service for Docker; references Core (see
  [Run in Docker](#run-in-docker-headless)).

- **Harvesting engine** (`DropHarvester.Core/Services/Twitch/`) - pure .NET (`HttpClient`,
  `ClientWebSocket`, `System.Text.Json`), identical on both OSes:
  - `TwitchAuthService` - device-code OAuth + token validation/persistence.
  - `GqlClient` - Twitch private GraphQL (persisted queries + the raw watch
    mutation), with bounded retry/backoff on transient failures.
  - `InventoryService` - campaign/drop discovery, progress sync, claiming, and the
    claimed-reward history.
  - `WatchService` - the minute-watched heartbeat via the `sendSpadeEvents`
    GraphQL mutation (gzip+base64 payload).
  - `WebsocketPool` - sharded PubSub (LISTEN/PING/reconnect).
  - `ChannelManager` - live drops-enabled channel discovery + stream state.
  - `HarvesterOrchestrator` - the run loop tying it together; emits `HarvesterEvent`s and
    exposes the debug snapshot.
- **Core services** (`DropHarvester.Core/Services/`) - `SettingsStore`, `StatsService`,
  `WebhookNotifier`, `DebugServer`, plus the `HarvesterEventBus`. App-only services stay in
  the app: `UpdateService` (installer-based updater) and `AlertsCoordinator` (bridges the
  event bus to notifications/webhooks/stats/tray/sound).
- **Update checker** (`Services/UpdateService.cs`) - polls a self-hosted JSON
  manifest for the latest per-OS version (on startup and once every 24 hours while
  running); Windows self-install + relaunch. No GitHub or tokens required.
- **UI** - MAUI Shell tabs (Status, Campaigns, Channels, Stats, Settings, Log),
  MVVM via CommunityToolkit.Mvvm, fed by an in-process `HarvesterEventBus`. Observable
  models and collections marshal changes to the UI thread via an injectable
  `IUiDispatcher` - the desktop app backs it with MAUI's main thread; the headless
  daemon runs everything inline.
- **Platform code** (`Platforms/{Windows,MacCatalyst}/`) - tray, notifications,
  autostart, and the drop-claimed sound (NAudio on Windows, AVFoundation on macOS)
  behind shared interfaces, selected in `MauiProgram`.

Persisted files live in the app data folder: `auth.json`, `settings.json`,
`stats.json`.

## Building

```bash
# Windows
dotnet build DropHarvester.csproj -c Release -f net10.0-windows10.0.19041.0

# macOS (Mac Catalyst)
dotnet build DropHarvester.csproj -c Release -f net10.0-maccatalyst
```

## Updating Twitch API constants

If Twitch rotates a GraphQL persisted-query hash or a client id, everything is
centralized in `Models/Twitch/TwitchConstants.cs` - update it there.
