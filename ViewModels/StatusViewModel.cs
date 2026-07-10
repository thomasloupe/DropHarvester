using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using DropHarvester.Localization;
using DropHarvester.Models;
using DropHarvester.Models.Events;
using DropHarvester.Models.Twitch;
using DropHarvester.Services;
using DropHarvester.Services.Twitch;

namespace DropHarvester.ViewModels;

/// <summary>
/// Drives the Status tab: login (device-code flow) plus live harvesting status (active channel and
/// drop, websocket health, start/pause) fed by the harvester event bus.
/// </summary>
public partial class StatusViewModel : ObservableViewModel
{
    readonly ITwitchAuth _auth;
    readonly IHarvesterOrchestrator _harvester;
    readonly IHarvesterEventBus _bus;
    readonly IUpdateService _update;
    readonly ISettingsStore _settings;
    CancellationTokenSource? _loginCts;

    /// <summary>Subscribes to auth and harvester-event notifications, syncs login state, starts the per-second
    /// countdown, sets the version label, resumes harvesting if already logged in, and kicks off update checks.</summary>
    /// <param name="auth">Twitch authentication service.</param>
    /// <param name="harvester">Harvester orchestrator that runs the harvesting loop.</param>
    /// <param name="bus">Event bus carrying live harvester status events.</param>
    /// <param name="update">Update service for checking and downloading new versions.</param>
    /// <param name="settings">Persisted settings store.</param>
    public StatusViewModel(ITwitchAuth auth, IHarvesterOrchestrator harvester, IHarvesterEventBus bus,
        IUpdateService update, ISettingsStore settings)
    {
        _auth = auth;
        _harvester = harvester;
        _bus = bus;
        _update = update;
        _settings = settings;

        _auth.AuthChanged += OnAuthChanged;
        _bus.Event += OnHarvesterEvent;
        Loc.Changed += () =>
        {
            OnPropertyChanged(nameof(StartPauseText));
            OnPropertyChanged(nameof(ConnectionIssueText));
            SyncFromAuth(); // re-translate the account line ("Logged in as ...") in the new language
            // the "Watching X - Game" summary is set once from a harvesting event, so re-compose it here;
            // idle summaries re-emit from the loop within a minute
            if (HarvestingActive && _activeCampaign is { } camp && ActiveChannelName != "-")
                HarvestingSummary = Loc.T("Status_WatchingChannelGame", ActiveChannelName, camp.Game.Name);
        };
        SyncFromAuth();
        _ = RunCountdownAsync();

        VersionText = $"v{_update.CurrentVersion}";
        if (_auth.IsLoggedIn)
            _ = StartAfterRevalidateAsync();
        RefreshPendingUpdate();
        if (_settings.Settings.AutoCheckForUpdates)
        {
            _ = CheckForUpdateBannerAsync();
            _ = PollForUpdatesAsync();
        }
    }

    /// <summary>Refresh the active drop/campaign "time remaining" every second so the countdown ticks
    /// down smoothly (the model interpolates between whole-minute progress updates).</summary>
    async Task RunCountdownAsync()
    {
        var timer = new PeriodicTimer(TimeSpan.FromSeconds(1));
        try
        {
            while (await timer.WaitForNextTickAsync())
            {
                ActiveDropRemainingText = _activeDrop is { HasRemaining: true } d ? d.RemainingText : "";
                CampaignRemainingText = _activeCampaign is { HasRemaining: true } c ? c.RemainingText : "";
            }
        }
        catch { /* app shutting down */ }
    }

    /// <summary>While the app stays open, re-check for a newer release once every 24 hours and raise the
    /// "update available" banner if one appears.</summary>
    async Task PollForUpdatesAsync()
    {
        var timer = new PeriodicTimer(TimeSpan.FromHours(24));
        try
        {
            while (await timer.WaitForNextTickAsync())
            {
                if (_settings.Settings.AutoCheckForUpdates)
                    await CheckForUpdateBannerAsync();
            }
        }
        catch { /* app shutting down */ }
    }

    [ObservableProperty] private bool _isLoggedIn;
    [ObservableProperty] private string _accountLine = "Not logged in";
    [ObservableProperty] private bool _loginInProgress;
    [ObservableProperty] private bool _showDeviceCode;
    [ObservableProperty] private string _deviceCode = "";
    [ObservableProperty] private string _verificationUri = "";
    [ObservableProperty] private string _loginHint = "";

    [ObservableProperty] private bool _harvestingActive;
    [ObservableProperty] private string _harvestingSummary = "Idle";
    [ObservableProperty] private string _activeChannelName = "-";
    // login of the active channel, for the "Watch Live" button's twitch.tv URL
    string? _activeChannelLogin;
    [ObservableProperty] private bool _canWatchLive;
    [ObservableProperty] private string _activeGameName = "-";
    [ObservableProperty] private string _activeCampaignName = "-";
    [ObservableProperty] private double _campaignProgress;
    [ObservableProperty] private string _campaignProgressText = "";
    [ObservableProperty] private string _activeDropName = "-";
    [ObservableProperty] private double _activeDropProgress;
    [ObservableProperty] private string _activeDropProgressText = "";
    [ObservableProperty] private string? _activeDropImageUrl;
    [ObservableProperty] private string _websocketStatus = "disconnected";

    [ObservableProperty] private string _campaignRemainingText = "";
    [ObservableProperty] private string _activeDropRemainingText = "";

    // "Up next" = the campaign the harvester will move to after the active one, plus its drops.
    [ObservableProperty] private bool _nextUpVisible;
    [ObservableProperty] private string _nextUpGameName = "-";
    [ObservableProperty] private string? _nextUpDropImageUrl;
    [ObservableProperty] private string? _nextUpCampaignId; // the "Harvest" button's override target
    /// <summary>All of the up-next campaign's drops, in order - listed under the game on the Status tab.</summary>
    public ObservableCollection<TimedDrop> NextUpDrops { get; } = new UiObservableCollection<TimedDrop>();

    /// <summary>The ordered list of harvestable campaigns; click one to override, or clear the override.</summary>
    public ObservableCollection<HarvestingQueueItem> Queue { get; } = new UiObservableCollection<HarvestingQueueItem>();
    [ObservableProperty] private bool _queueVisible;
    [ObservableProperty] private bool _overrideActive;

    /// <summary>Remembered: while an override is active, let a campaign that newly appeared after the
    /// override AND ranks higher in the effective order (ending-soonest, availability, or priority list)
    /// end it automatically (off = the override runs to completion / manual removal; a campaign already
    /// known when the override was set never ends it).</summary>
    public bool OverrideYieldsToPriority
    {
        get => _settings.Settings.OverrideYieldsToPriority;
        set
        {
            if (value == _settings.Settings.OverrideYieldsToPriority)
                return;
            _settings.Settings.OverrideYieldsToPriority = value;
            _settings.Save();
            OnPropertyChanged();
        }
    }

    /// <summary>Applies a manual campaign override, resolving drop-only vs entire-campaign from the
    /// override-mode setting (prompting the user when set to AskMe).</summary>
    /// <param name="campaignId">Id of the campaign to override to; ignored when null or empty.</param>
    [RelayCommand]
    async Task HarvestCampaign(string? campaignId)
    {
        if (string.IsNullOrEmpty(campaignId))
            return;

        bool dropOnly;
        switch (_settings.Settings.OverrideMode)
        {
            case OverrideMode.DropOnly: dropOnly = true; break;
            case OverrideMode.EntireCampaign: dropOnly = false; break;
            default: // AskMe - prompt the user for this override
                var justDrop = Loc.T("Override_JustNextDrop");
                var entire = Loc.T("Override_EntireCampaign");
                var choice = await Shell.Current.DisplayActionSheetAsync(
                    Loc.T("Override_HarvestPrompt"), Loc.T("Common_Cancel"), null, justDrop, entire);
                if (choice == justDrop) dropOnly = true;
                else if (choice == entire) dropOnly = false;
                else return; // cancelled
                break;
        }
        _harvester.SetCampaignOverride(campaignId, dropOnly);
    }

    /// <summary>Clears any active manual campaign override.</summary>
    [RelayCommand]
    void RemoveOverride() => _harvester.ClearCampaignOverride();

    // Active drop/campaign models, re-read each second so their countdowns tick down to the second.
    TimedDrop? _activeDrop;
    DropsCampaign? _activeCampaign;

    [ObservableProperty] private bool _connectionIssueVisible;
    public string ConnectionIssueText => Loc.T("Status_ConnectionIssue");

    // A newer GitHub release surfaces two ways: an "available" banner (checked, not yet downloaded) with a
    // Download button, and - once downloaded - the "ready" banner with Update now. On the next launch a
    // downloaded-but-unapplied update auto-installs.
    [ObservableProperty] private string _versionText = "";
    [ObservableProperty] private bool _updateReady;        // a newer installer is downloaded + ready
    [ObservableProperty] private bool _updateAvailable;    // a newer release exists (not yet downloaded)
    [ObservableProperty] private bool _downloadingUpdate;  // the Download button is mid-download
    [ObservableProperty] private string _latestVersion = "";
    [ObservableProperty] private string _updateStatus = "";
    UpdateInfo? _availableInfo;                            // cached "available" check result (asset url)

    /// <summary>Raises change notifications for the "ready" and "available" banner visibility when
    /// UpdateReady changes (the two are mutually exclusive).</summary>
    /// <param name="value">The new UpdateReady value.</param>
    partial void OnUpdateReadyChanged(bool value)
    {
        OnPropertyChanged(nameof(UpdateReadyHidden));
        OnPropertyChanged(nameof(UpdateAvailableVisible));
    }
    public bool UpdateReadyHidden => !UpdateReady;

    /// <summary>Notifies the "available" banner's visibility + text when availability changes.</summary>
    /// <param name="value">The new UpdateAvailable value.</param>
    partial void OnUpdateAvailableChanged(bool value)
    {
        OnPropertyChanged(nameof(UpdateAvailableVisible));
        OnPropertyChanged(nameof(UpdateAvailableDetail));
    }
    /// <summary>Notifies the "available" banner's text + the Download button's enabled state when the
    /// download state changes.</summary>
    /// <param name="value">The new DownloadingUpdate value.</param>
    partial void OnDownloadingUpdateChanged(bool value)
    {
        OnPropertyChanged(nameof(UpdateAvailableDetail));
        OnPropertyChanged(nameof(CanDownloadUpdate));
    }

    /// <summary>Whether the Download button is clickable (disabled while a download is in flight).</summary>
    public bool CanDownloadUpdate => !DownloadingUpdate;
    /// <summary>Notifies the "available" banner's text when the latest version changes.</summary>
    /// <param name="value">The new LatestVersion value.</param>
    partial void OnLatestVersionChanged(string value) => OnPropertyChanged(nameof(UpdateAvailableDetail));

    /// <summary>The "update available" banner shows only when a newer release exists AND we haven't already
    /// downloaded it (once downloaded, the "Update now" banner takes over).</summary>
    public bool UpdateAvailableVisible => UpdateAvailable && !UpdateReady;

    string _downloadProgressText = ""; // " 45% (3.2 MB/s)" appended to the downloading line

    /// <summary>The "available" banner's detail line: the recommend-to-update message, or a downloading
    /// note (with percent + speed) while the Download button is working.</summary>
    public string UpdateAvailableDetail => DownloadingUpdate
        ? Loc.T("Status_UpdateDownloading", LatestVersion) + _downloadProgressText
        : Loc.T("Status_UpdateAvailableDetail", LatestVersion);

    /// <summary>Reflect whether a pending installer is already downloaded (e.g. fetched last session or
    /// via Settings' check). Safe to call any time - the Status page calls it when it appears.</summary>
    public void RefreshPendingUpdate()
    {
        var pending = _update.PendingVersion;
        UpdateReady = pending is not null;
        if (UpdateReady)
            UpdateStatus = $"Update v{pending} is ready.";
    }

    /// <summary>Check GitHub for a newer release and, if one exists, raise the "update available" banner.
    /// Does NOT download - the user starts that with the Download button. Silent on failure.</summary>
    async Task CheckForUpdateBannerAsync()
    {
        try
        {
            RefreshPendingUpdate();
            if (UpdateReady)
                return; // a downloaded update already waits -> its own "Update now" banner shows

            var info = await _update.CheckAsync();
            if (info.Error is not null || !info.UpdateAvailable)
                return; // stay quiet on failure / when current

            _availableInfo = info;
            LatestVersion = info.LatestVersion ?? "";
            UpdateAvailable = true;
        }
        catch { /* best-effort, silent */ }
    }

    /// <summary>Download the available release's installer to the pending cache (the Download button on the
    /// "update available" banner). When it lands, the "Update now" banner takes over.</summary>
    [RelayCommand]
    async Task DownloadUpdateAsync()
    {
        if (DownloadingUpdate)
            return;
        // re-check if the asset url isn't cached, so the button always works
        var info = _availableInfo ?? await _update.CheckAsync();
        if (!info.UpdateAvailable)
        {
            UpdateAvailable = false;
            return;
        }
        _downloadProgressText = "";
        DownloadingUpdate = true;
        try
        {
            var progress = new Progress<UpdateProgress>(p =>
            {
                var speed = p.BytesPerSecond > 0 ? $" ({p.BytesPerSecond / (1024.0 * 1024.0):0.0} MB/s)" : "";
                _downloadProgressText = $" {(int)(p.Fraction * 100)}%{speed}";
                OnPropertyChanged(nameof(UpdateAvailableDetail));
            });
            var ok = await _update.DownloadAsync(info, progress);
            RefreshPendingUpdate();
            if (ok)
                UpdateAvailable = false; // hand off to the "Update now" banner
        }
        catch { /* leave the banner up so the user can retry */ }
        finally { DownloadingUpdate = false; }
    }

    /// <summary>Installs the downloaded update by launching the installer and hard-exiting the app.</summary>
    [RelayCommand]
    async Task UpdateNowAsync()
    {
        if (!UpdateReady)
            return;
        UpdateStatus = "Installing update - the app will restart...";
        await Task.Delay(400); // let the status paint
        // ApplyPending launches the installer (waits for us to close, installs, relaunches). Exit hard:
        // Application.Quit() is intercepted by the hide-to-tray handler, which would keep our files +
        // single-instance mutex locked so the install can't complete.
        if (_update.ApplyPending())
            Environment.Exit(0);
        else
            UpdateStatus = "Couldn't start the installer - it'll auto-install on next restart.";
    }

    /// <summary>Opens the active channel's Twitch page in the default browser so the user can watch the
    /// stream they're harvesting.</summary>
    [RelayCommand]
    async Task WatchLiveAsync()
    {
        if (string.IsNullOrEmpty(_activeChannelLogin))
            return;
        try { await Launcher.Default.OpenAsync($"https://www.twitch.tv/{_activeChannelLogin}"); }
        catch { /* no browser / user cancelled */ }
    }

    public string StartPauseText => HarvestingActive
        ? Loc.T("Status_PauseHarvesting")
        : Loc.T("Status_StartHarvesting");

    /// <summary>Raises a change notification for StartPauseText when HarvestingActive changes.</summary>
    /// <param name="value">The new HarvestingActive value.</param>
    partial void OnHarvestingActiveChanged(bool value) => OnPropertyChanged(nameof(StartPauseText));

    /// <summary>Toggles harvesting on or off (no-op unless logged in), then syncs HarvestingActive to the harvester.</summary>
    [RelayCommand]
    async Task ToggleHarvestingAsync()
    {
        if (!IsLoggedIn)
            return;
        if (_harvester.IsRunning)
            await _harvester.StopAsync();
        else
            await _harvester.StartAsync();
        HarvestingActive = _harvester.IsRunning;
    }

    /// <summary>Runs the Twitch device-code login flow: shows the code, waits for authorization, and starts the harvester on success.</summary>
    [RelayCommand]
    async Task LogInAsync()
    {
        if (LoginInProgress)
            return;

        LoginInProgress = true;
        LoginHint = Loc.T("Login_Requesting");
        _loginCts = new CancellationTokenSource();
        try
        {
            var device = await _auth.BeginDeviceLoginAsync(_loginCts.Token);

            DeviceCode = device.UserCode;
            VerificationUri = string.IsNullOrWhiteSpace(device.VerificationUri)
                ? "https://www.twitch.tv/activate"
                : device.VerificationUri;
            LoginHint = Loc.T("Login_GoToAndEnter", VerificationUri, device.UserCode);
            ShowDeviceCode = true;

            try { await Launcher.Default.OpenAsync(VerificationUri); } catch { }

            var ok = await _auth.AwaitAuthorizationAsync(device, _loginCts.Token);
            LoginHint = ok ? "" : Loc.T("Login_NotCompleted");
            if (ok)
                await _harvester.StartAsync();
        }
        catch (OperationCanceledException)
        {
            LoginHint = Loc.T("Login_Cancelled");
        }
        catch (Exception ex)
        {
            LoginHint = Loc.T("Login_Failed", ex.Message);
        }
        finally
        {
            ShowDeviceCode = false;
            LoginInProgress = false;
            _loginCts?.Dispose();
            _loginCts = null;
            SyncFromAuth();
            HarvestingActive = _harvester.IsRunning;
        }
    }

    /// <summary>Cancels an in-progress login attempt.</summary>
    [RelayCommand]
    void CancelLogin() => _loginCts?.Cancel();

    /// <summary>Stops harvesting, logs out of Twitch, and resets the status display to idle.</summary>
    [RelayCommand]
    async Task LogOutAsync()
    {
        await _harvester.StopAsync();
        _auth.LogOut();
        HarvestingActive = false;
        HarvestingSummary = Loc.T("Status_Idle");
        ActiveChannelName = "-";
        _activeChannelLogin = null;
        CanWatchLive = false;
        ActiveDropName = "-";
        ActiveDropProgress = 0;
        ActiveDropProgressText = "";
    }

    /// <summary>Revalidates the stored session, syncs auth state, and starts harvesting if still logged in.</summary>
    async Task StartAfterRevalidateAsync()
    {
        try { await _auth.ValidateAsync(); } catch { }
        SyncFromAuth();
        if (_auth.IsLoggedIn)
        {
            await _harvester.StartAsync();
            HarvestingActive = _harvester.IsRunning;
        }
    }

    /// <summary>Handles a harvester event on the UI thread, updating the bound status, queue, next-up, and connection properties.</summary>
    /// <param name="e">The harvester event to apply.</param>
    void OnHarvesterEvent(HarvesterEvent e) => MainThread.BeginInvokeOnMainThread(() =>
    {
        switch (e)
        {
            case HarvestingStateEvent m:
                HarvestingActive = m.Active;
                HarvestingSummary = m.Summary;
                if (!m.Active)
                {
                    ConnectionIssueVisible = false;
                    _activeDrop = null; _activeCampaign = null;
                    _activeChannelLogin = null; CanWatchLive = false;
                    ActiveDropRemainingText = ""; CampaignRemainingText = "";
                    NextUpVisible = false; NextUpDrops.Clear(); NextUpCampaignId = null;
                    Queue.Clear(); QueueVisible = false; OverrideActive = false;
                }
                break;

            case NextUpEvent nu:
                NextUpVisible = nu.Campaign is not null;
                NextUpGameName = nu.Campaign?.Game.Name ?? "-";
                NextUpCampaignId = nu.Campaign?.Id;
                NextUpDropImageUrl = nu.Drop?.RewardImageUrl ?? nu.Campaign?.ImageUrl;
                NextUpDrops.Clear();
                if (nu.Campaign is not null)
                    foreach (var d in nu.Campaign.Drops.OrderBy(d => d.RequiredMinutes))
                        NextUpDrops.Add(d);
                break;

            case HarvestingQueueEvent q:
                Queue.Clear();
                foreach (var it in q.Items) Queue.Add(it);
                QueueVisible = q.Items.Count > 0;
                OverrideActive = q.OverrideActive;
                break;

            case ConnectionIssueEvent ci:
                ConnectionIssueVisible = ci.HasIssue;
                break;

            case ActiveTargetEvent t:
                ActiveChannelName = t.Channel?.DisplayName ?? "-";
                _activeChannelLogin = t.Channel?.Login;
                CanWatchLive = !string.IsNullOrEmpty(_activeChannelLogin);
                ActiveGameName = t.Campaign?.Game.Name ?? "-";
                ActiveCampaignName = t.Campaign?.Name ?? "-";
                _activeCampaign = t.Campaign;
                CampaignRemainingText = t.Campaign is { HasRemaining: true } ac ? ac.RemainingText : "";
                if (t.Campaign is not null)
                {
                    CampaignProgress = t.Campaign.Progress;
                    CampaignProgressText = t.Campaign.OverallText;
                }
                UpdateDrop(t.Drop);
                break;
            case DropProgressEvent d:
                UpdateDrop(d.Drop);
                if (d.Drop.Campaign is { } camp)
                {
                    CampaignProgress = camp.Progress;
                    CampaignProgressText = camp.OverallText;
                }
                break;
            case WebsocketStatusEvent w:
                ApplyWebsocketStatus(w);
                break;
            case LoginExpiredEvent:
                LoginHint = Loc.T("Login_Expired");
                SyncFromAuth();
                break;
        }
    });

    WebsocketStatusEvent? _lastWs;
    int _wsStatusGen;

    /// <summary>Set the websocket status line. "connected" and "disconnected" show at once; "connecting"
    /// shows only after the pool stays not-fully-connected for a short grace period, so a quick reconnect
    /// keeps the indicator steady on "connected" rather than blinking between the two.</summary>
    /// <param name="w">The latest websocket status event.</param>
    void ApplyWebsocketStatus(WebsocketStatusEvent w)
    {
        _lastWs = w;
        if (w.Shards == 0)
        {
            _wsStatusGen++;
            WebsocketStatus = "disconnected";
            return;
        }
        if (w.AllConnected)
        {
            _wsStatusGen++; // supersedes any pending "connecting"
            WebsocketStatus = WsLine(w, "connected");
            return;
        }
        var gen = ++_wsStatusGen;
        _ = ShowConnectingIfStillDownAsync(gen);
    }

    /// <summary>After the grace period, show "connecting" only if this is still the latest status and the
    /// pool is still not fully connected.</summary>
    /// <param name="gen">The status generation this call waits on.</param>
    async Task ShowConnectingIfStillDownAsync(int gen)
    {
        try { await Task.Delay(TimeSpan.FromSeconds(8)).ConfigureAwait(false); }
        catch { return; }
        MainThread.BeginInvokeOnMainThread(() =>
        {
            if (gen != _wsStatusGen || _lastWs is not { } w || w.AllConnected || w.Shards == 0)
                return;
            WebsocketStatus = WsLine(w, "connecting");
        });
    }

    /// <summary>Formats the websocket status line for a shard/topic count and state word.</summary>
    /// <param name="w">The status event supplying the shard/topic counts.</param>
    /// <param name="state">The connection state word to show.</param>
    static string WsLine(WebsocketStatusEvent w, string state) => $"{w.Shards} shard(s) - {w.Topics} topic(s) - {state}";

    /// <summary>Updates the active-drop display (name, progress, remaining, image) from the given drop, clearing it when null.</summary>
    /// <param name="drop">The active drop to show, or null to clear the display.</param>
    void UpdateDrop(TimedDrop? drop)
    {
        _activeDrop = drop;
        if (drop is null)
        {
            ActiveDropName = "-";
            ActiveDropProgress = 0;
            ActiveDropProgressText = "";
            ActiveDropRemainingText = "";
            ActiveDropImageUrl = null;
            return;
        }
        ActiveDropName = drop.RewardName;
        ActiveDropRemainingText = drop.HasRemaining ? drop.RemainingText : "";
        ActiveDropProgress = drop.Progress;
        ActiveDropProgressText = drop.ProgressSummary;
        ActiveDropImageUrl = drop.RewardImageUrl;
    }

    /// <summary>Handles auth changes by syncing login state on the UI thread.</summary>
    void OnAuthChanged() => MainThread.BeginInvokeOnMainThread(SyncFromAuth);

    /// <summary>Refreshes IsLoggedIn and the account line from the current auth state.</summary>
    void SyncFromAuth()
    {
        IsLoggedIn = _auth.IsLoggedIn;
        var who = string.IsNullOrEmpty(_auth.State.DisplayName) ? _auth.State.Username : _auth.State.DisplayName;
        AccountLine = _auth.IsLoggedIn ? Loc.T("Status_LoggedInAs", who, _auth.State.UserId) : Loc.T("Status_NotLoggedIn");
    }
}
