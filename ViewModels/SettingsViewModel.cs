using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using DropHarvester.Localization;
using DropHarvester.Models;
using DropHarvester.Services;
using DropHarvester.Services.Twitch;

namespace DropHarvester.ViewModels;

/// <summary>
/// Settings tab: priority/exclusion lists (with live Twitch game autocomplete + validation),
/// harvesting behavior, notifications and webhook config. Mirrors <see cref="AppSettings"/> into
/// observable properties and saves on every change (the Cowculator pattern).
/// </summary>
public partial class SettingsViewModel : ObservableViewModel
{
    readonly ISettingsStore _store;
    readonly IHarvesterOrchestrator _harvester;
    readonly IWebhookNotifier _webhook;
    readonly IAutostartService _autostartService;
    readonly IGameSearchService _search;
    readonly ISoundService _sound;
    readonly IDebugServer _debug;
    readonly IUpdateService _update;
    readonly AppSettings _s;
    bool _loading;
    List<AudioDevice> _audioDevices = new();
    CancellationTokenSource? _prioritySearchCts;
    CancellationTokenSource? _excludeSearchCts;
    CancellationTokenSource? _dedupeSearchCts;
    CancellationTokenSource? _unlinkedSearchCts;

    /// <summary>Identifies which game list an autocomplete search or selection targets.</summary>
    enum ListKind { Priority, Excluded, Dedupe, Unlinked }

    /// <summary>The languages offered in the Language dropdown, each as (native display name, culture code).
    /// English is the base; the others fall back to English for anything not yet translated.</summary>
    static readonly (string Name, string Code)[] Languages =
    {
        ("English", "en"),
        ("Español", "es"),
        ("Français", "fr"),
        ("Deutsch", "de"),
        ("Русский", "ru"),
        ("简体中文", "zh-Hans"),
        ("日本語", "ja"),
        ("한국어", "ko"),
        ("Nederlands", "nl"),
    };

    /// <summary>Native-spelling language names shown in the Language picker.</summary>
    public IReadOnlyList<string> LanguageNames { get; } = Languages.Select(l => l.Name).ToList();

    [ObservableProperty] private int _languageIndex;

    /// <summary>Persists the picked language and applies it live (English fallback for missing strings).</summary>
    /// <param name="value">The selected index into <see cref="LanguageNames"/>.</param>
    partial void OnLanguageIndexChanged(int value)
    {
        if (_loading || value < 0 || value >= Languages.Length)
            return;
        _s.Language = Languages[value].Code;
        _store.Save();
        Loc.Culture = Languages[value].Code;
    }

    /// <summary>Localized labels for the override-mode picker, in enum order (whole campaign, next drop, ask).</summary>
    public IReadOnlyList<string> OverrideModeNames => new[]
    {
        Loc.T("Override_EntireCampaign"),
        Loc.T("Override_JustNextDrop"),
        Loc.T("Override_AskEachTime"),
    };

    public ObservableCollection<string> PriorityGames { get; } = new UiObservableCollection<string>();
    public ObservableCollection<string> ExcludedGames { get; } = new UiObservableCollection<string>();
    public ObservableCollection<string> DedupeGames { get; } = new UiObservableCollection<string>();
    public ObservableCollection<string> HarvestUnlinkedGames { get; } = new UiObservableCollection<string>();
    public ObservableCollection<string> PreferredChannels { get; } = new UiObservableCollection<string>();
    public ObservableCollection<string> AvoidedChannels { get; } = new UiObservableCollection<string>();
    public ObservableCollection<GameMatch> PrioritySuggestions { get; } = new UiObservableCollection<GameMatch>();
    public ObservableCollection<GameMatch> ExcludedSuggestions { get; } = new UiObservableCollection<GameMatch>();
    public ObservableCollection<GameMatch> DedupeSuggestions { get; } = new UiObservableCollection<GameMatch>();
    public ObservableCollection<GameMatch> UnlinkedSuggestions { get; } = new UiObservableCollection<GameMatch>();

    /// <summary>Mirrors the persisted settings into observable properties, loads audio devices, and
    /// subscribes to channel-preference changes.</summary>
    /// <param name="store">persisted settings store.</param>
    /// <param name="harvester">orchestrator notified when settings that affect harvesting change.</param>
    /// <param name="webhook">notifier used to send webhook test messages.</param>
    /// <param name="autostart">service that toggles OS autostart.</param>
    /// <param name="search">Twitch game search used for autocomplete and validation.</param>
    /// <param name="sound">drop-claimed sound playback service.</param>
    /// <param name="debug">local debug HTTP server.</param>
    /// <param name="update">app update checker/installer.</param>
    public SettingsViewModel(
        ISettingsStore store, IHarvesterOrchestrator harvester, IWebhookNotifier webhook,
        IAutostartService autostart, IGameSearchService search, ISoundService sound, IDebugServer debug,
        IUpdateService update)
    {
        _store = store;
        _harvester = harvester;
        _webhook = webhook;
        _autostartService = autostart;
        _search = search;
        _sound = sound;
        _debug = debug;
        _update = update;
        _s = store.Settings;

        _loading = true;
        foreach (var g in _s.PriorityGames) PriorityGames.Add(g);
        foreach (var g in _s.ExcludedGames) ExcludedGames.Add(g);
        foreach (var g in _s.DedupeGames) DedupeGames.Add(g);
        foreach (var g in _s.HarvestUnlinkedGames) HarvestUnlinkedGames.Add(g);
        foreach (var c in _s.PreferredChannels) PreferredChannels.Add(c);
        foreach (var c in _s.AvoidedChannels) AvoidedChannels.Add(c);
        // Keep these lists in sync when they're edited from the Channels tab's right-click menu.
        _harvester.ChannelPreferencesChanged += OnChannelPreferencesChanged;
        // re-translate this VM's computed strings + picker items live when the language changes
        Loc.Changed += () => OnPropertyChanged(string.Empty);

        PriorityOnly = _s.PriorityOnly;
        EndingSoonest = _s.EndingSoonest;
        AvailabilityPriority = _s.AvailabilityPriority;
        HarvestImpossibleDrops = _s.HarvestImpossibleDrops;
        ShowUnlinkedInChannels = _s.ShowUnlinkedInChannels;
        EnableBadgesEmotes = _s.EnableBadgesEmotes;
        HarvestSubDrops = _s.HarvestSubDrops;
        AutoClaimChannelPoints = _s.AutoClaimChannelPoints;
        OverrideModeIndex = (int)_s.OverrideMode;
        var langIdx = Array.FindIndex(Languages, l => string.Equals(l.Code, _s.Language, StringComparison.OrdinalIgnoreCase));
        LanguageIndex = langIdx >= 0 ? langIdx : 0;
        Proxy = _s.Proxy ?? "";
        MinimizeToTray = _s.MinimizeToTray;
        Autostart = _s.Autostart;
        AutostartIntoTray = _s.AutostartIntoTray;
        NotifyOnDropClaimed = _s.NotifyOnDropClaimed;
        NotifyOnCampaignComplete = _s.NotifyOnCampaignComplete;
        NotifyOnAllHarvested = _s.NotifyOnAllHarvested;
        NotifyOnLoginExpired = _s.NotifyOnLoginExpired;
        WebhookEnabled = _s.WebhookEnabled;
        WebhookUrl = _s.WebhookUrl ?? "";
        WebhookKindIndex = (int)_s.WebhookKind;
        WebhookOnNewDrop = _s.WebhookOnNewDrop;
        WebhookOnDropClaimed = _s.WebhookOnDropClaimed;
        WebhookOnCampaignComplete = _s.WebhookOnCampaignComplete;
        WebhookOnAllHarvested = _s.WebhookOnAllHarvested;
        WebhookOnLoginExpired = _s.WebhookOnLoginExpired;
        LogTimestampModeIndex = (int)_s.LogTimestampMode;
        LogClockIndex = _s.LogUse24Hour ? 1 : 0;
        AutoCheckForUpdates = _s.AutoCheckForUpdates;
        DebugServerEnabled = _s.DebugServerEnabled;
        DebugServerPortText = _s.DebugServerPort.ToString();

        // ----- drop-claimed sound -----
        AudioSupported = _sound.IsSupported;
        PlaySoundOnDropClaimed = _s.PlaySoundOnDropClaimed;
        DropClaimedSoundPath = _s.DropClaimedSoundPath ?? "";
        SoundVolume = Math.Clamp(_s.DropClaimedSoundVolume, 0.0, 1.0);
        _audioDevices = _sound.GetOutputDevices().ToList();
        foreach (var d in _audioDevices) AudioDeviceNames.Add(d.Name);
        var savedIdx = _audioDevices.FindIndex(d => string.Equals(d.Id, _s.AudioOutputDeviceId ?? "", StringComparison.OrdinalIgnoreCase));
        AudioDeviceIndex = savedIdx >= 0 ? savedIdx : 0;

        _loading = false;
    }

    // ----- drop-claimed sound -----
    public ObservableCollection<string> AudioDeviceNames { get; } = new UiObservableCollection<string>();
    [ObservableProperty] private bool _audioSupported;
    [ObservableProperty] private bool _playSoundOnDropClaimed;
    [ObservableProperty] private string _dropClaimedSoundPath = "";
    [ObservableProperty] private int _audioDeviceIndex;
    [ObservableProperty] private double _soundVolume = 1.0; // 0.0 - 1.0
    [ObservableProperty] private string _soundStatus = "";

    /// <summary>Volume as a whole percentage, for the slider label.</summary>
    public string SoundVolumeText => $"{(int)Math.Round(SoundVolume * 100)}%";

    /// <summary>Updates the volume label and saves when the sound volume changes.</summary>
    /// <param name="value">the new volume (0.0 - 1.0).</param>
    partial void OnSoundVolumeChanged(double value)
    {
        OnPropertyChanged(nameof(SoundVolumeText));
        Save();
    }

    /// <summary>Just the file name for display (full path is stored in settings).</summary>
    public string SoundFileDisplay =>
        string.IsNullOrEmpty(DropClaimedSoundPath) ? Loc.T("Settings_NoSoundChosen") : Path.GetFileName(DropClaimedSoundPath);

    /// <summary>Refreshes the displayed sound file name when the sound path changes.</summary>
    /// <param name="value">the new sound file path.</param>
    partial void OnDropClaimedSoundPathChanged(string value) => OnPropertyChanged(nameof(SoundFileDisplay));
    /// <summary>Saves when the play-sound-on-claim toggle changes.</summary>
    /// <param name="value">the new toggle value.</param>
    partial void OnPlaySoundOnDropClaimedChanged(bool value) => Save();
    /// <summary>Saves when the selected audio output device changes.</summary>
    /// <param name="value">the new device index.</param>
    partial void OnAudioDeviceIndexChanged(int value) => Save();

    /// <summary>Prompts for an audio file and stores it as the drop-claimed sound.</summary>
    [RelayCommand]
    async Task PickSoundFileAsync()
    {
        try
        {
            var audio = new FilePickerFileType(new Dictionary<DevicePlatform, IEnumerable<string>>
            {
                [DevicePlatform.WinUI] = new[] { ".wav", ".mp3", ".m4a", ".aac", ".wma", ".aiff", ".aif", ".ogg" },
                [DevicePlatform.MacCatalyst] = new[] { "public.audio" },
            });
            var result = await FilePicker.Default.PickAsync(new PickOptions
            {
                PickerTitle = Loc.T("Settings_ChooseSoundTitle"),
                FileTypes = audio,
            });
            if (result is null)
                return; // cancelled
            DropClaimedSoundPath = result.FullPath;
            SoundStatus = "";
            Save();
        }
        catch (Exception ex)
        {
            SoundStatus = Loc.T("Settings_SoundPickFailed", ex.Message);
        }
    }

    /// <summary>Clears the chosen drop-claimed sound.</summary>
    [RelayCommand]
    void ClearSoundFile()
    {
        DropClaimedSoundPath = "";
        SoundStatus = "";
        Save();
    }

    /// <summary>Plays the chosen sound on the selected device to preview it.</summary>
    [RelayCommand]
    void TestSound()
    {
        if (!_sound.IsSupported)
        {
            SoundStatus = Loc.T("Settings_AudioNotAvailable");
            return;
        }
        if (string.IsNullOrEmpty(DropClaimedSoundPath))
        {
            SoundStatus = Loc.T("Settings_ChooseSoundFirst");
            return;
        }
        var deviceId = AudioDeviceIndex >= 0 && AudioDeviceIndex < _audioDevices.Count
            ? _audioDevices[AudioDeviceIndex].Id : null;
        var deviceName = AudioDeviceIndex >= 0 && AudioDeviceIndex < _audioDevices.Count
            ? _audioDevices[AudioDeviceIndex].Name : Loc.T("Settings_SystemDefault");
        SoundStatus = Loc.T("Settings_PlayingOn", deviceName, SoundVolumeText);
        _sound.Play(DropClaimedSoundPath, deviceId, SoundVolume);
    }

    // ----- list entry text -----
    [ObservableProperty] private string _newPriorityGame = "";
    [ObservableProperty] private string _newExcludedGame = "";
    [ObservableProperty] private string _newDedupeGame = "";
    [ObservableProperty] private string _newUnlinkedGame = "";
    [ObservableProperty] private string _priorityHint = "";
    [ObservableProperty] private string _excludedHint = "";
    [ObservableProperty] private string _dedupeHint = "";
    [ObservableProperty] private string _unlinkedHint = "";

    // ----- toggles / fields -----
    [ObservableProperty] private bool _priorityOnly;
    [ObservableProperty] private bool _endingSoonest;
    [ObservableProperty] private bool _availabilityPriority;
    [ObservableProperty] private bool _harvestImpossibleDrops;
    [ObservableProperty] private bool _showUnlinkedInChannels;
    [ObservableProperty] private bool _enableBadgesEmotes;
    [ObservableProperty] private bool _harvestSubDrops;
    [ObservableProperty] private bool _autoClaimChannelPoints;
    [ObservableProperty] private string _proxy = "";
    [ObservableProperty] private bool _minimizeToTray;
    [ObservableProperty] private bool _autostart;
    [ObservableProperty] private bool _autostartIntoTray;
    [ObservableProperty] private bool _notifyOnDropClaimed;
    [ObservableProperty] private bool _notifyOnCampaignComplete;
    [ObservableProperty] private bool _notifyOnAllHarvested;
    [ObservableProperty] private bool _notifyOnLoginExpired;
    [ObservableProperty] private bool _webhookEnabled;
    [ObservableProperty] private string _webhookUrl = "";
    [ObservableProperty] private int _webhookKindIndex; // 0 Discord, 1 Slack, 2 Generic
    [ObservableProperty] private string _webhookTestResult = "";
    [ObservableProperty] private bool _showWebhookUrl;
    [ObservableProperty] private bool _webhookOnNewDrop;
    [ObservableProperty] private bool _webhookOnDropClaimed;
    [ObservableProperty] private bool _webhookOnCampaignComplete;
    [ObservableProperty] private bool _webhookOnAllHarvested;
    [ObservableProperty] private bool _webhookOnLoginExpired;

    // ----- log display -----
    [ObservableProperty] private int _logTimestampModeIndex; // Date / Time / Date+time / Time+date
    [ObservableProperty] private int _logClockIndex;         // 0 = 12-hour, 1 = 24-hour
    public string[] LogTimestampModes => new[]
    {
        Loc.T("Settings_TimestampDate"), Loc.T("Settings_TimestampTime"),
        Loc.T("Settings_TimestampDateAndTime"), Loc.T("Settings_TimestampTimeAndDate"),
    };
    public string[] LogClocks => new[] { Loc.T("Settings_Clock12Hour"), Loc.T("Settings_Clock24Hour") };

    // ----- updates -----
    [ObservableProperty] private bool _autoCheckForUpdates;
    [ObservableProperty] private string _updateCheckStatus = "";
    [ObservableProperty] private bool _checkingForUpdates;
    [ObservableProperty] private bool _updateReady; // a newer installer is downloaded + ready to install

    /// <summary>Current version, shown next to the manual check button.</summary>
    public string CurrentVersionText => Loc.T("Settings_CurrentVersion", _update.CurrentVersion);

    /// <summary>Reflect whether a pending installer is already downloaded (from a prior check/session, the
    /// background auto-check, or the Status tab) so the "Update now" button shows here too. Called when
    /// the Settings page appears.</summary>
    public void RefreshPendingUpdate()
    {
        var pending = _update.PendingVersion;
        UpdateReady = pending is not null;
        if (UpdateReady && string.IsNullOrEmpty(UpdateCheckStatus))
            UpdateCheckStatus = Loc.T("Settings_UpdateReadyStatus", pending!);
    }

    /// <summary>Checks for a newer version and downloads the installer if one is available.</summary>
    [RelayCommand]
    async Task CheckForUpdatesAsync()
    {
        if (CheckingForUpdates)
            return;
        CheckingForUpdates = true;
        UpdateCheckStatus = Loc.T("Settings_CheckingForUpdates");
        try
        {
            var info = await _update.CheckAsync();
            if (info.Error is not null)
                UpdateCheckStatus = Loc.T("Settings_UpdateServerUnreachable");
            else if (!info.UpdateAvailable)
                UpdateCheckStatus = Loc.T("Settings_OnLatestVersion", info.CurrentVersion);
            else
            {
                UpdateCheckStatus = Loc.T("Settings_DownloadingUpdate", info.LatestVersion);
                var progress = new Progress<UpdateProgress>(p =>
                {
                    var speed = p.BytesPerSecond > 0 ? $" ({p.BytesPerSecond / (1024.0 * 1024.0):0.0} MB/s)" : "";
                    UpdateCheckStatus = $"{Loc.T("Settings_DownloadingUpdate", info.LatestVersion)} {(int)(p.Fraction * 100)}%{speed}";
                });
                var ok = await _update.DownloadAsync(info, progress);
                RefreshPendingUpdate();
                UpdateCheckStatus = ok
                    ? Loc.T("Settings_UpdateReadyAutoInstall", info.LatestVersion)
                    : Loc.T("Settings_UpdateDownloadFailed");
            }
        }
        catch { UpdateCheckStatus = Loc.T("Settings_UpdateCheckFailed"); }
        finally { CheckingForUpdates = false; }
    }

    /// <summary>Install the downloaded update now (same as the Status tab's button): launch the installer,
    /// then exit hard so it can replace our files. Application.Quit() is intercepted by hide-to-tray, which
    /// would keep the files + single-instance mutex locked, so the install couldn't complete.</summary>
    [RelayCommand]
    async Task UpdateNowAsync()
    {
        if (!UpdateReady)
            return;
        UpdateCheckStatus = Loc.T("Settings_InstallingUpdate");
        await Task.Delay(400); // let the status paint
        if (_update.ApplyPending())
            Environment.Exit(0);
        else
            UpdateCheckStatus = Loc.T("Settings_InstallerStartFailed");
    }

    // ----- debug server -----
    [ObservableProperty] private bool _debugServerEnabled;
    [ObservableProperty] private string _debugServerPortText = "5757";

    public string DebugServerStatus => _debug.IsRunning
        ? Loc.T("Settings_DebugServerRunning", $"http://localhost:{_debug.Port}/")
        : Loc.T("Settings_DebugServerOff");

    /// <summary>Starts or stops the debug server when its enabled toggle changes.</summary>
    /// <param name="value">the new toggle value.</param>
    partial void OnDebugServerEnabledChanged(bool value) => ApplyDebugServer();
    /// <summary>Re-applies the debug server when its port text changes.</summary>
    /// <param name="value">the new port text.</param>
    partial void OnDebugServerPortTextChanged(string value) => ApplyDebugServer();

    /// <summary>Persists the debug server settings and starts or stops it to match.</summary>
    void ApplyDebugServer()
    {
        if (_loading) return;
        _s.DebugServerEnabled = DebugServerEnabled;
        if (int.TryParse(DebugServerPortText, out var p) && p is > 0 and < 65536)
            _s.DebugServerPort = p;
        _store.Save();
        if (_s.DebugServerEnabled)
            _debug.Start(_s.DebugServerPort);
        else
            _debug.Stop();
        OnPropertyChanged(nameof(DebugServerStatus));
    }

    public bool WebhookUrlMasked => !ShowWebhookUrl;
    public string WebhookUrlToggleText => ShowWebhookUrl ? Loc.T("Settings_Hide") : Loc.T("Settings_Show");
    /// <summary>Refreshes the masked-URL view properties when the show-webhook-URL toggle changes.</summary>
    /// <param name="value">the new toggle value.</param>
    partial void OnShowWebhookUrlChanged(bool value)
    {
        OnPropertyChanged(nameof(WebhookUrlMasked));
        OnPropertyChanged(nameof(WebhookUrlToggleText));
    }

    /// <summary>Toggles masking of the webhook URL.</summary>
    [RelayCommand]
    void ToggleWebhookUrl() => ShowWebhookUrl = !ShowWebhookUrl;

    public string[] WebhookKinds => new[] { "Discord", "Slack", Loc.T("Settings_WebhookGeneric") };

    /// <summary>Saves the webhook config and sends a test message, reporting the result.</summary>
    [RelayCommand]
    async Task TestWebhookAsync()
    {
        Save();
        WebhookTestResult = Loc.T("Settings_Sending");
        WebhookTestResult = await _webhook.SendTestAsync();
    }

    // ----- game autocomplete -----
    /// <summary>Runs a game autocomplete search for the priority list as its input changes.</summary>
    /// <param name="value">the current search text.</param>
    partial void OnNewPriorityGameChanged(string value) => _ = SearchAsync(value, PrioritySuggestions, ListKind.Priority);
    /// <summary>Runs a game autocomplete search for the excluded list as its input changes.</summary>
    /// <param name="value">the current search text.</param>
    partial void OnNewExcludedGameChanged(string value) => _ = SearchAsync(value, ExcludedSuggestions, ListKind.Excluded);
    /// <summary>Runs a game autocomplete search for the de-dupe list as its input changes.</summary>
    /// <param name="value">the current search text.</param>
    partial void OnNewDedupeGameChanged(string value) => _ = SearchAsync(value, DedupeSuggestions, ListKind.Dedupe);
    /// <summary>Runs a game autocomplete search for the harvest-unlinked list as its input changes.</summary>
    /// <param name="value">the current search text.</param>
    partial void OnNewUnlinkedGameChanged(string value) => _ = SearchAsync(value, UnlinkedSuggestions, ListKind.Unlinked);

    /// <summary>Debounced, cancelable Twitch game search that fills the target suggestion list.</summary>
    /// <param name="query">the search text.</param>
    /// <param name="target">the suggestion collection to populate.</param>
    /// <param name="kind">which list the search is for (drives per-list hint and cancellation).</param>
    async Task SearchAsync(string query, ObservableCollection<GameMatch> target, ListKind kind)
    {
        switch (kind)
        {
            case ListKind.Priority: PriorityHint = ""; break;
            case ListKind.Excluded: ExcludedHint = ""; break;
            case ListKind.Dedupe: DedupeHint = ""; break;
            case ListKind.Unlinked: UnlinkedHint = ""; break;
        }

        var cts = new CancellationTokenSource();
        switch (kind)
        {
            case ListKind.Priority: _prioritySearchCts?.Cancel(); _prioritySearchCts = cts; break;
            case ListKind.Excluded: _excludeSearchCts?.Cancel(); _excludeSearchCts = cts; break;
            case ListKind.Dedupe: _dedupeSearchCts?.Cancel(); _dedupeSearchCts = cts; break;
            case ListKind.Unlinked: _unlinkedSearchCts?.Cancel(); _unlinkedSearchCts = cts; break;
        }

        if (string.IsNullOrWhiteSpace(query) || query.Trim().Length < 2)
        {
            target.Clear();
            return;
        }

        try
        {
            await Task.Delay(250, cts.Token); // debounce
            var matches = await _search.SearchAsync(query, cts.Token);
            if (cts.Token.IsCancellationRequested) return;

            MainThread.BeginInvokeOnMainThread(() =>
            {
                target.Clear();
                foreach (var m in matches)
                    target.Add(m);
            });
        }
        catch (OperationCanceledException) { }
        catch { }
    }

    /// <summary>Adds the chosen suggestion to the priority list.</summary>
    /// <param name="match">the selected game suggestion, or null.</param>
    [RelayCommand]
    void SelectPriority(GameMatch? match)
    {
        if (match is not null) AddPriorityGame(match.Name);
    }

    /// <summary>Adds the chosen suggestion to the excluded list.</summary>
    /// <param name="match">the selected game suggestion, or null.</param>
    [RelayCommand]
    void SelectExcluded(GameMatch? match)
    {
        if (match is not null) AddExcludedGame(match.Name);
    }

    // ----- priority list commands -----
    /// <summary>Validates the typed priority game and adds it, or shows a hint if invalid.</summary>
    [RelayCommand]
    async Task AddPriorityAsync()
    {
        var name = await ValidateGameAsync(NewPriorityGame, PrioritySuggestions);
        if (name is null) { PriorityHint = Loc.T("Settings_InvalidGameHint"); return; }
        AddPriorityGame(name);
    }

    /// <summary>Adds a game to the priority list (if new), persists, and clears the input.</summary>
    /// <param name="name">the canonical game name to add.</param>
    void AddPriorityGame(string name)
    {
        if (!PriorityGames.Contains(name, StringComparer.OrdinalIgnoreCase))
        {
            PriorityGames.Add(name);
            PersistLists();
        }
        NewPriorityGame = "";
        PrioritySuggestions.Clear();
        PriorityHint = "";
    }

    /// <summary>Removes a game from the priority list and persists.</summary>
    /// <param name="game">the game to remove, or null.</param>
    [RelayCommand]
    void RemovePriority(string? game)
    {
        if (game is not null && PriorityGames.Remove(game))
            PersistLists();
    }

    /// <summary>Moves a priority game up one position and persists.</summary>
    /// <param name="game">the game to move, or null.</param>
    [RelayCommand]
    void MoveUp(string? game)
    {
        if (game is null) return;
        var i = PriorityGames.IndexOf(game);
        if (i > 0) { PriorityGames.Move(i, i - 1); PersistLists(); }
    }

    /// <summary>Moves a priority game down one position and persists.</summary>
    /// <param name="game">the game to move, or null.</param>
    [RelayCommand]
    void MoveDown(string? game)
    {
        if (game is null) return;
        var i = PriorityGames.IndexOf(game);
        if (i >= 0 && i < PriorityGames.Count - 1) { PriorityGames.Move(i, i + 1); PersistLists(); }
    }

    /// <summary>Drag-reorder step: move <paramref name="game"/> to <paramref name="index"/> WITHOUT saving
    /// (called live while dragging). Call <see cref="CommitPriorityOrder"/> once the drag ends.</summary>
    /// <param name="game">the game being dragged, or null.</param>
    /// <param name="index">the target position (clamped to the list bounds).</param>
    public void MovePriorityToIndex(string? game, int index)
    {
        if (game is null) return;
        var from = PriorityGames.IndexOf(game);
        if (from < 0) return;
        index = Math.Clamp(index, 0, PriorityGames.Count - 1);
        if (index != from) PriorityGames.Move(from, index);
    }

    /// <summary>Persist the priority order after a drag-reorder finishes.</summary>
    public void CommitPriorityOrder() => PersistLists();

    // ----- excluded list commands -----
    /// <summary>Validates the typed excluded game and adds it, or shows a hint if invalid.</summary>
    [RelayCommand]
    async Task AddExcludedAsync()
    {
        var name = await ValidateGameAsync(NewExcludedGame, ExcludedSuggestions);
        if (name is null) { ExcludedHint = Loc.T("Settings_InvalidGameHint"); return; }
        AddExcludedGame(name);
    }

    /// <summary>Adds a game to the excluded list (if new), persists, and clears the input.</summary>
    /// <param name="name">the canonical game name to add.</param>
    void AddExcludedGame(string name)
    {
        if (!ExcludedGames.Contains(name, StringComparer.OrdinalIgnoreCase))
        {
            ExcludedGames.Add(name);
            PersistLists();
        }
        NewExcludedGame = "";
        ExcludedSuggestions.Clear();
        ExcludedHint = "";
    }

    /// <summary>Removes a game from the excluded list and persists.</summary>
    /// <param name="game">the game to remove, or null.</param>
    [RelayCommand]
    void RemoveExcluded(string? game)
    {
        if (game is not null && ExcludedGames.Remove(game))
            PersistLists();
    }

    // ----- de-dupe list commands -----
    /// <summary>Adds the chosen suggestion to the de-dupe list.</summary>
    /// <param name="match">the selected game suggestion, or null.</param>
    [RelayCommand]
    void SelectDedupe(GameMatch? match)
    {
        if (match is not null) AddDedupeGame(match.Name);
    }

    /// <summary>Validates the typed de-dupe game and adds it, or shows a hint if invalid.</summary>
    [RelayCommand]
    async Task AddDedupeAsync()
    {
        var name = await ValidateGameAsync(NewDedupeGame, DedupeSuggestions);
        if (name is null) { DedupeHint = Loc.T("Settings_InvalidGameHint"); return; }
        AddDedupeGame(name);
    }

    /// <summary>Adds a game to the de-dupe list (if new), persists, and clears the input.</summary>
    /// <param name="name">the canonical game name to add.</param>
    void AddDedupeGame(string name)
    {
        if (!DedupeGames.Contains(name, StringComparer.OrdinalIgnoreCase))
        {
            DedupeGames.Add(name);
            PersistLists();
        }
        NewDedupeGame = "";
        DedupeSuggestions.Clear();
        DedupeHint = "";
    }

    /// <summary>Removes a game from the de-dupe list and persists.</summary>
    /// <param name="game">the game to remove, or null.</param>
    [RelayCommand]
    void RemoveDedupe(string? game)
    {
        if (game is not null && DedupeGames.Remove(game))
            PersistLists();
    }

    // ----- harvest-unlinked list commands -----
    /// <summary>Adds the chosen suggestion to the harvest-unlinked list.</summary>
    /// <param name="match">the selected game suggestion, or null.</param>
    [RelayCommand]
    void SelectUnlinked(GameMatch? match)
    {
        if (match is not null) AddUnlinkedGame(match.Name);
    }

    /// <summary>Validates the typed harvest-unlinked game and adds it, or shows a hint if invalid.</summary>
    [RelayCommand]
    async Task AddUnlinkedAsync()
    {
        var name = await ValidateGameAsync(NewUnlinkedGame, UnlinkedSuggestions);
        if (name is null) { UnlinkedHint = Loc.T("Settings_InvalidGameHint"); return; }
        AddUnlinkedGame(name);
    }

    /// <summary>Adds a game to the harvest-unlinked list (if new), persists, and clears the input.</summary>
    /// <param name="name">the canonical game name to add.</param>
    void AddUnlinkedGame(string name)
    {
        if (!HarvestUnlinkedGames.Contains(name, StringComparer.OrdinalIgnoreCase))
        {
            HarvestUnlinkedGames.Add(name);
            PersistLists();
        }
        NewUnlinkedGame = "";
        UnlinkedSuggestions.Clear();
        UnlinkedHint = "";
    }

    /// <summary>Removes a game from the harvest-unlinked list and persists.</summary>
    /// <param name="game">the game to remove, or null.</param>
    [RelayCommand]
    void RemoveUnlinked(string? game)
    {
        if (game is not null && HarvestUnlinkedGames.Remove(game))
            PersistLists();
    }

    // ----- preferred / avoided channels (added via right-click in the Channels tab) -----
    /// <summary>Reloads the preferred/avoided channel lists from settings when they change elsewhere.</summary>
    void OnChannelPreferencesChanged() => MainThread.BeginInvokeOnMainThread(() =>
    {
        PreferredChannels.Clear();
        foreach (var c in _s.PreferredChannels) PreferredChannels.Add(c);
        AvoidedChannels.Clear();
        foreach (var c in _s.AvoidedChannels) AvoidedChannels.Add(c);
    });

    /// <summary>Removes a channel from the preferred list and syncs the Channels tab.</summary>
    /// <param name="login">the channel login to remove, or null.</param>
    [RelayCommand]
    void RemovePreferredChannel(string? login)
    {
        if (login is null) return;
        _s.PreferredChannels.RemoveAll(l => string.Equals(l, login, StringComparison.OrdinalIgnoreCase));
        PreferredChannels.Remove(login);
        _store.Save();
        _harvester.SyncChannelPreferences(); // update the Channels tab badges
    }

    /// <summary>Removes a channel from the avoided list and syncs the Channels tab.</summary>
    /// <param name="login">the channel login to remove, or null.</param>
    [RelayCommand]
    void RemoveAvoidedChannel(string? login)
    {
        if (login is null) return;
        _s.AvoidedChannels.RemoveAll(l => string.Equals(l, login, StringComparison.OrdinalIgnoreCase));
        AvoidedChannels.Remove(login);
        _store.Save();
        _harvester.SyncChannelPreferences();
    }

    // Typed entry (in addition to right-click in the Channels tab).
    [ObservableProperty] private string _newPreferredChannel = "";
    [ObservableProperty] private string _newAvoidedChannel = "";

    /// <summary>Adds a typed channel to the preferred list, removing it from avoided (mutually exclusive).</summary>
    [RelayCommand]
    void AddPreferredChannel()
    {
        var login = NormalizeLogin(NewPreferredChannel);
        NewPreferredChannel = "";
        if (login is null) return;
        // Prefer and avoid are mutually exclusive.
        _s.AvoidedChannels.RemoveAll(l => string.Equals(l, login, StringComparison.OrdinalIgnoreCase));
        var dup = AvoidedChannels.FirstOrDefault(l => string.Equals(l, login, StringComparison.OrdinalIgnoreCase));
        if (dup is not null) AvoidedChannels.Remove(dup);
        if (!_s.PreferredChannels.Contains(login, StringComparer.OrdinalIgnoreCase))
        {
            _s.PreferredChannels.Add(login);
            PreferredChannels.Add(login);
        }
        _store.Save();
        _harvester.SyncChannelPreferences();
    }

    /// <summary>Adds a typed channel to the avoided list, removing it from preferred (mutually exclusive).</summary>
    [RelayCommand]
    void AddAvoidedChannel()
    {
        var login = NormalizeLogin(NewAvoidedChannel);
        NewAvoidedChannel = "";
        if (login is null) return;
        _s.PreferredChannels.RemoveAll(l => string.Equals(l, login, StringComparison.OrdinalIgnoreCase));
        var dup = PreferredChannels.FirstOrDefault(l => string.Equals(l, login, StringComparison.OrdinalIgnoreCase));
        if (dup is not null) PreferredChannels.Remove(dup);
        if (!_s.AvoidedChannels.Contains(login, StringComparer.OrdinalIgnoreCase))
        {
            _s.AvoidedChannels.Add(login);
            AvoidedChannels.Add(login);
        }
        _store.Save();
        _harvester.SyncChannelPreferences();
    }

    /// <summary>Reduce a typed value to a Twitch login: strips a pasted twitch.tv URL, a leading '@',
    /// and any query/fragment; lowercases. Returns null if it isn't a valid login (letters/digits/_).</summary>
    /// <param name="raw">the raw typed or pasted value.</param>
    static string? NormalizeLogin(string? raw)
    {
        if (string.IsNullOrWhiteSpace(raw)) return null;
        var s = raw.Trim();
        var slash = s.LastIndexOf('/');
        if (slash >= 0) s = s[(slash + 1)..];      // keep the last path segment of a URL
        s = s.TrimStart('@').Trim();
        foreach (var sep in new[] { '?', '#', ' ' }) // drop query/fragment/trailing junk
        {
            var i = s.IndexOf(sep);
            if (i >= 0) s = s[..i];
        }
        s = s.ToLowerInvariant();
        return s.Length > 0 && s.All(ch => char.IsLetterOrDigit(ch) || ch == '_') ? s : null;
    }

    /// <summary>Return the canonical Twitch game name if the typed text matches a real game, else null.</summary>
    /// <param name="typed">the text the user typed.</param>
    /// <param name="suggestions">the current suggestion list, checked for an exact match first.</param>
    async Task<string?> ValidateGameAsync(string typed, ObservableCollection<GameMatch> suggestions)
    {
        typed = typed.Trim();
        if (typed.Length == 0) return null;

        var exact = suggestions.FirstOrDefault(m => string.Equals(m.Name, typed, StringComparison.OrdinalIgnoreCase));
        if (exact is not null) return exact.Name;

        var matches = await _search.SearchAsync(typed);
        return matches.FirstOrDefault(m => string.Equals(m.Name, typed, StringComparison.OrdinalIgnoreCase))?.Name;
    }

    // ----- persistence -----
    /// <summary>Writes the four game lists to settings, saves, and asks the harvester to refresh.</summary>
    void PersistLists()
    {
        _s.PriorityGames = PriorityGames.ToList();
        _s.ExcludedGames = ExcludedGames.ToList();
        _s.DedupeGames = DedupeGames.ToList();
        _s.HarvestUnlinkedGames = HarvestUnlinkedGames.ToList();
        _store.Save();
        _harvester.RequestRefresh();
    }

    /// <summary>Copies every scalar setting from the observable properties into the store and saves
    /// (no-op while the constructor is loading).</summary>
    void Save()
    {
        if (_loading) return;
        _s.PriorityOnly = PriorityOnly;
        _s.EndingSoonest = EndingSoonest;
        _s.AvailabilityPriority = AvailabilityPriority;
        _s.HarvestImpossibleDrops = HarvestImpossibleDrops;
        _s.ShowUnlinkedInChannels = ShowUnlinkedInChannels;
        _s.EnableBadgesEmotes = EnableBadgesEmotes;
        _s.HarvestSubDrops = HarvestSubDrops;
        _s.AutoClaimChannelPoints = AutoClaimChannelPoints;
        _s.OverrideMode = (OverrideMode)OverrideModeIndex;
        _s.Proxy = string.IsNullOrWhiteSpace(Proxy) ? null : Proxy.Trim();
        _s.MinimizeToTray = MinimizeToTray;
        _s.Autostart = Autostart;
        _s.AutostartIntoTray = AutostartIntoTray;
        _s.NotifyOnDropClaimed = NotifyOnDropClaimed;
        _s.NotifyOnCampaignComplete = NotifyOnCampaignComplete;
        _s.NotifyOnAllHarvested = NotifyOnAllHarvested;
        _s.NotifyOnLoginExpired = NotifyOnLoginExpired;
        _s.WebhookEnabled = WebhookEnabled;
        _s.WebhookUrl = string.IsNullOrWhiteSpace(WebhookUrl) ? null : WebhookUrl.Trim();
        _s.WebhookKind = (WebhookKind)WebhookKindIndex;
        _s.WebhookOnNewDrop = WebhookOnNewDrop;
        _s.WebhookOnDropClaimed = WebhookOnDropClaimed;
        _s.WebhookOnCampaignComplete = WebhookOnCampaignComplete;
        _s.WebhookOnAllHarvested = WebhookOnAllHarvested;
        _s.WebhookOnLoginExpired = WebhookOnLoginExpired;
        _s.LogTimestampMode = (LogTimestampMode)LogTimestampModeIndex;
        _s.LogUse24Hour = LogClockIndex == 1;
        _s.AutoCheckForUpdates = AutoCheckForUpdates;
        _s.DebugServerEnabled = DebugServerEnabled;
        if (int.TryParse(DebugServerPortText, out var dp) && dp is > 0 and < 65536)
            _s.DebugServerPort = dp;
        _s.PlaySoundOnDropClaimed = PlaySoundOnDropClaimed;
        _s.DropClaimedSoundPath = string.IsNullOrEmpty(DropClaimedSoundPath) ? null : DropClaimedSoundPath;
        _s.AudioOutputDeviceId = AudioDeviceIndex >= 0 && AudioDeviceIndex < _audioDevices.Count
            ? _audioDevices[AudioDeviceIndex].Id : null;
        _s.DropClaimedSoundVolume = Math.Clamp(SoundVolume, 0.0, 1.0);
        _store.Save();
    }

    /// <summary>Saves and refreshes the harvester when the priority-only toggle changes.</summary>
    /// <param name="value">the new toggle value.</param>
    partial void OnPriorityOnlyChanged(bool value) { Save(); _harvester.RequestRefresh(); }
    /// <summary>Saves and refreshes the harvester when the ending-soonest toggle changes.</summary>
    /// <param name="value">the new toggle value.</param>
    partial void OnEndingSoonestChanged(bool value) { Save(); _harvester.RequestRefresh(); }
    /// <summary>Saves and refreshes the harvester when the availability-priority toggle changes.</summary>
    /// <param name="value">the new toggle value.</param>
    partial void OnAvailabilityPriorityChanged(bool value) { Save(); _harvester.RequestRefresh(); }
    /// <summary>Saves and refreshes the harvester when the harvest-impossible-drops toggle changes.</summary>
    /// <param name="value">the new toggle value.</param>
    partial void OnHarvestImpossibleDropsChanged(bool value) { Save(); _harvester.RequestRefresh(); }
    /// <summary>Saves and refreshes the harvester when the show-unlinked-in-channels toggle changes.</summary>
    /// <param name="value">the new toggle value.</param>
    partial void OnShowUnlinkedInChannelsChanged(bool value) { Save(); _harvester.RequestRefresh(); }
    /// <summary>Saves and refreshes the harvester when the badges/emotes toggle changes.</summary>
    /// <param name="value">the new toggle value.</param>
    partial void OnEnableBadgesEmotesChanged(bool value) { Save(); _harvester.RequestRefresh(); }
    partial void OnHarvestSubDropsChanged(bool value) { Save(); _harvester.RequestRefresh(); }
    /// <summary>Saves when the auto-claim-channel-points toggle changes.</summary>
    /// <param name="value">the new toggle value.</param>
    partial void OnAutoClaimChannelPointsChanged(bool value) => Save();
    // 0 = Entire campaign, 1 = Just the next drop, 2 = Ask me each time (matches OverrideMode enum order).
    [ObservableProperty] private int _overrideModeIndex;
    /// <summary>Saves when the override-mode selection changes.</summary>
    /// <param name="value">the new override-mode index.</param>
    partial void OnOverrideModeIndexChanged(int value) => Save();
    /// <summary>Saves when the proxy text changes.</summary>
    /// <param name="value">the new proxy value.</param>
    partial void OnProxyChanged(string value) => Save();
    /// <summary>Saves when the minimize-to-tray toggle changes.</summary>
    /// <param name="value">the new toggle value.</param>
    partial void OnMinimizeToTrayChanged(bool value) => Save();
    /// <summary>Applies the OS autostart setting when the autostart toggle changes.</summary>
    /// <param name="value">the new toggle value.</param>
    partial void OnAutostartChanged(bool value) => ApplyAutostart();
    /// <summary>Applies the OS autostart setting when the autostart-into-tray toggle changes.</summary>
    /// <param name="value">the new toggle value.</param>
    partial void OnAutostartIntoTrayChanged(bool value) => ApplyAutostart();

    /// <summary>Saves and pushes the autostart preference to the OS when supported.</summary>
    void ApplyAutostart()
    {
        Save();
        if (_autostartService.IsSupported)
            _autostartService.SetEnabled(Autostart, AutostartIntoTray);
    }

    /// <summary>Saves when the notify-on-drop-claimed toggle changes.</summary>
    /// <param name="value">the new toggle value.</param>
    partial void OnNotifyOnDropClaimedChanged(bool value) => Save();
    /// <summary>Saves when the notify-on-campaign-complete toggle changes.</summary>
    /// <param name="value">the new toggle value.</param>
    partial void OnNotifyOnCampaignCompleteChanged(bool value) => Save();
    /// <summary>Saves when the notify-on-all-harvested toggle changes.</summary>
    /// <param name="value">the new toggle value.</param>
    partial void OnNotifyOnAllHarvestedChanged(bool value) => Save();
    /// <summary>Saves when the notify-on-login-expired toggle changes.</summary>
    /// <param name="value">the new toggle value.</param>
    partial void OnNotifyOnLoginExpiredChanged(bool value) => Save();
    /// <summary>Saves when the webhook-enabled toggle changes.</summary>
    /// <param name="value">the new toggle value.</param>
    partial void OnWebhookEnabledChanged(bool value) => Save();
    /// <summary>Saves when the webhook URL changes.</summary>
    /// <param name="value">the new URL value.</param>
    partial void OnWebhookUrlChanged(string value) => Save();
    /// <summary>Saves when the webhook kind selection changes.</summary>
    /// <param name="value">the new webhook-kind index.</param>
    partial void OnWebhookKindIndexChanged(int value) => Save();
    /// <summary>Saves when the webhook-on-new-drop toggle changes.</summary>
    /// <param name="value">the new toggle value.</param>
    partial void OnWebhookOnNewDropChanged(bool value) => Save();
    /// <summary>Saves when the webhook-on-drop-claimed toggle changes.</summary>
    /// <param name="value">the new toggle value.</param>
    partial void OnWebhookOnDropClaimedChanged(bool value) => Save();
    /// <summary>Saves when the webhook-on-campaign-complete toggle changes.</summary>
    /// <param name="value">the new toggle value.</param>
    partial void OnWebhookOnCampaignCompleteChanged(bool value) => Save();
    /// <summary>Saves when the webhook-on-all-harvested toggle changes.</summary>
    /// <param name="value">the new toggle value.</param>
    partial void OnWebhookOnAllHarvestedChanged(bool value) => Save();
    /// <summary>Saves when the webhook-on-login-expired toggle changes.</summary>
    /// <param name="value">the new toggle value.</param>
    partial void OnWebhookOnLoginExpiredChanged(bool value) => Save();
    /// <summary>Saves when the log timestamp mode changes.</summary>
    /// <param name="value">the new timestamp-mode index.</param>
    partial void OnLogTimestampModeIndexChanged(int value) => Save();
    /// <summary>Saves when the log clock (12/24-hour) selection changes.</summary>
    /// <param name="value">the new clock index.</param>
    partial void OnLogClockIndexChanged(int value) => Save();
    /// <summary>Saves when the auto-check-for-updates toggle changes.</summary>
    /// <param name="value">the new toggle value.</param>
    partial void OnAutoCheckForUpdatesChanged(bool value) => Save();
}
