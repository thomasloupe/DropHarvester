using System.Runtime.InteropServices;
using DropHarvester.Services;
using Microsoft.UI.Windowing;
using WinRT.Interop;
using MuxWindow = Microsoft.UI.Xaml.Window;

namespace DropHarvester.Platforms.Windows;

/// <summary>
/// System-tray presence on Windows via Shell_NotifyIcon. Closing the window hides it to the tray
/// (keeping the harvester running) when that setting is on; the tray icon restores it, and a
/// right-click menu offers Open / Quit. The window proc is subclassed to receive tray clicks.
/// </summary>
public sealed class WindowsTrayService : ITrayService
{
    const int WM_APP = 0x8000;
    const int WM_TRAYICON = WM_APP + 1;
    const int WM_LBUTTONUP = 0x0202;
    const int WM_LBUTTONDBLCLK = 0x0203;
    const int WM_RBUTTONUP = 0x0205;
    const int WM_COMMAND = 0x0111;
    const uint NIM_ADD = 0, NIM_MODIFY = 1, NIM_DELETE = 2;
    const uint NIF_MESSAGE = 0x1, NIF_ICON = 0x2, NIF_TIP = 0x4, NIF_INFO = 0x10;
    const uint TPM_RIGHTBUTTON = 0x2, TPM_RETURNCMD = 0x100;
    const uint MF_STRING = 0x0;
    const int IDM_OPEN = 1001, IDM_QUIT = 1002;

    readonly ISettingsStore _settings;

    Microsoft.Maui.Controls.Window? _mauiWindow;
    MuxWindow? _nativeWindow;
    AppWindow? _appWindow;
    IntPtr _hwnd;
    NativeMethods.SUBCLASSPROC? _subclass; // kept rooted to avoid GC
    bool _iconAdded;
    bool _quitting;
    bool _startHidden; // "autostart into tray" - keep re-hiding through the startup show sequence
    int _hideTicks;    // bounded guard so the repeating start-hidden timer can't run forever

    /// <summary>Stores the settings store used to decide hide-to-tray and start-hidden behavior.</summary>
    /// <param name="settings">The application settings store.</param>
    public WindowsTrayService(ISettingsStore settings) => _settings = settings;

    public bool IsSupported => true;

    /// <summary>Remembers the MAUI window that the tray icon controls.</summary>
    /// <param name="window">The MAUI application window.</param>
    public void AttachWindow(Microsoft.Maui.Controls.Window window) => _mauiWindow = window;

    /// <summary>Wires the tray icon, window-proc subclass, hide-to-tray close handler, and start-hidden behavior once the native window exists.</summary>
    /// <param name="nativeWindow">The native WinUI window backing the MAUI window.</param>
    public void InitializeNative(MuxWindow nativeWindow)
    {
        _nativeWindow = nativeWindow;
        _hwnd = WindowNative.GetWindowHandle(nativeWindow);
        var id = Microsoft.UI.Win32Interop.GetWindowIdFromWindow(_hwnd);
        _appWindow = AppWindow.GetFromWindowId(id);

        if (_appWindow is not null)
            _appWindow.Closing += OnClosing;

        // Subclass the window proc to receive tray-icon callbacks.
        _subclass = SubclassProc;
        NativeMethods.SetWindowSubclass(_hwnd, _subclass, (nuint)1, (nuint)0);

        AddIcon();

        // Launched via "autostart into tray"? Start hidden and STAY hidden through WinUI's startup show
        // sequence - activation AND the window-bounds restore both re-show the window, so a one-shot
        // Hide() doesn't stick. Read the RAW command line (GetCommandLineArgs can drop the arg on some
        // WinUI activation paths); then guard three ways: hide now, re-hide on every visibility change,
        // and hide on a short repeating timer to catch a late show that doesn't raise Changed.
        // Start hidden on EVERY launch (manual or autostart) when the setting is on - driven by the
        // setting itself, not just the --tray arg the autostart entry passes. So it no longer depends on
        // "Start with the system".
        var startInTray = _settings.Settings.AutostartIntoTray
            || Environment.CommandLine.Contains("--tray", StringComparison.OrdinalIgnoreCase);
        if (startInTray && _appWindow is not null)
        {
            _startHidden = true;
            _appWindow.Changed += KeepHiddenOnStartup;
            HideNow();
            Microsoft.Maui.Controls.Application.Current?.Dispatcher.StartTimer(TimeSpan.FromMilliseconds(120), () =>
            {
                if (!_startHidden || _hideTicks++ > 16) // ~2s of guarding, or until the user opens it
                    return false;
                HideNow();
                return true;
            });
        }
    }

    /// <summary>Hide the window both ways - AppWindow.Hide() and a Win32 SW_HIDE - for good measure.</summary>
    void HideNow()
    {
        _appWindow?.Hide();
        if (_hwnd != IntPtr.Zero)
            NativeMethods.ShowWindow(_hwnd, 0 /* SW_HIDE */);
    }

    /// <summary>Re-hides the window whenever WinUI's startup sequence makes it visible again.</summary>
    /// <param name="sender">The native app window that changed.</param>
    /// <param name="args">The change details, including visibility change.</param>
    void KeepHiddenOnStartup(AppWindow sender, AppWindowChangedEventArgs args)
    {
        if (_startHidden && args.DidVisibilityChange && sender.IsVisible)
            HideNow();
    }

    /// <summary>Cancels window close and hides to the tray instead when minimize-to-tray is enabled.</summary>
    /// <param name="sender">The native app window being closed.</param>
    /// <param name="args">The closing event args, whose Cancel is set to keep the app running.</param>
    void OnClosing(AppWindow sender, AppWindowClosingEventArgs args)
    {
        if (_quitting || !_settings.Settings.MinimizeToTray)
            return;
        args.Cancel = true;
        sender.Hide();
    }

    /// <summary>Adds the system-tray icon with its callback message, icon, and tooltip.</summary>
    void AddIcon()
    {
        if (_hwnd == IntPtr.Zero)
            return;

        var data = NewIconData();
        data.uFlags = NIF_MESSAGE | NIF_ICON | NIF_TIP;
        data.uCallbackMessage = WM_TRAYICON;
        data.hIcon = LoadAppIcon();
        SetTip(ref data, "DropHarvester");
        _iconAdded = NativeMethods.Shell_NotifyIcon(NIM_ADD, ref data);
    }

    /// <summary>Updates the tray icon's tooltip to reflect the current status.</summary>
    /// <param name="status">The status text appended to the tooltip.</param>
    public void SetStatus(string status)
    {
        if (!_iconAdded)
            return;
        var data = NewIconData();
        data.uFlags = NIF_TIP;
        SetTip(ref data, Trim($"DropHarvester - {status}", 127));
        NativeMethods.Shell_NotifyIcon(NIM_MODIFY, ref data);
    }

    /// <summary>Restores and foregrounds the window, disabling the start-hidden guard so it stays open.</summary>
    public void ShowWindow()
    {
        // The user explicitly asked to see it - stop the startup keep-hidden guard so it stays open.
        if (_startHidden)
        {
            _startHidden = false;
            if (_appWindow is not null)
                _appWindow.Changed -= KeepHiddenOnStartup;
        }
        _appWindow?.Show();
        if (_hwnd != IntPtr.Zero)
        {
            NativeMethods.ShowWindow(_hwnd, 9 /* SW_RESTORE */);
            NativeMethods.SetForegroundWindow(_hwnd);
        }
    }

    /// <summary>Window subclass procedure that handles tray-icon click and menu-command messages, delegating the rest to the default handler.</summary>
    /// <param name="hWnd">The window receiving the message.</param>
    /// <param name="msg">The Win32 message id.</param>
    /// <param name="wParam">Message-specific first parameter.</param>
    /// <param name="lParam">Message-specific second parameter.</param>
    /// <param name="uIdSubclass">The subclass identifier.</param>
    /// <param name="dwRefData">Caller-supplied reference data.</param>
    /// <returns>The message result, or the default subclass handler's result for unhandled messages.</returns>
    IntPtr SubclassProc(IntPtr hWnd, uint msg, IntPtr wParam, IntPtr lParam, nuint uIdSubclass, nuint dwRefData)
    {
        switch (msg)
        {
            case WM_TRAYICON:
                var mouse = (int)(lParam.ToInt64() & 0xFFFF);
                if (mouse is WM_LBUTTONUP or WM_LBUTTONDBLCLK)
                    ShowWindow();
                else if (mouse == WM_RBUTTONUP)
                    ShowContextMenu();
                return IntPtr.Zero;

            case WM_COMMAND:
                var cmd = (int)(wParam.ToInt64() & 0xFFFF);
                if (cmd == IDM_OPEN) { ShowWindow(); return IntPtr.Zero; }
                if (cmd == IDM_QUIT) { QuitApp(); return IntPtr.Zero; }
                break;
        }
        return NativeMethods.DefSubclassProc(hWnd, msg, wParam, lParam);
    }

    /// <summary>Shows the tray right-click menu (Open / Quit) at the cursor and acts on the chosen item.</summary>
    void ShowContextMenu()
    {
        var menu = NativeMethods.CreatePopupMenu();
        if (menu == IntPtr.Zero)
            return;
        NativeMethods.AppendMenu(menu, MF_STRING, IDM_OPEN, "Open DropHarvester");
        NativeMethods.AppendMenu(menu, MF_STRING, IDM_QUIT, "Quit");

        NativeMethods.GetCursorPos(out var pt);
        NativeMethods.SetForegroundWindow(_hwnd); // required so the menu dismisses correctly
        var cmd = NativeMethods.TrackPopupMenu(menu, TPM_RIGHTBUTTON | TPM_RETURNCMD, pt.X, pt.Y, 0, _hwnd, IntPtr.Zero);
        NativeMethods.DestroyMenu(menu);
        if (cmd == IDM_OPEN) ShowWindow();
        else if (cmd == IDM_QUIT) QuitApp();
    }

    /// <summary>Removes the tray icon and quits the application on the main thread.</summary>
    void QuitApp()
    {
        _quitting = true;
        RemoveIcon();
        MainThread.BeginInvokeOnMainThread(() => Microsoft.Maui.Controls.Application.Current?.Quit());
    }

    /// <summary>Deletes the system-tray icon if it was added.</summary>
    void RemoveIcon()
    {
        if (!_iconAdded)
            return;
        var data = NewIconData();
        NativeMethods.Shell_NotifyIcon(NIM_DELETE, ref data);
        _iconAdded = false;
    }

    /// <summary>Builds a NOTIFYICONDATA seeded with this window's handle and the shared icon id.</summary>
    NativeMethods.NOTIFYICONDATA NewIconData() => new()
    {
        cbSize = Marshal.SizeOf<NativeMethods.NOTIFYICONDATA>(),
        hWnd = _hwnd,
        uID = 1,
    };

    /// <summary>Sets the tooltip text field on the given icon data.</summary>
    /// <param name="data">The icon data to modify.</param>
    /// <param name="tip">The tooltip text.</param>
    static void SetTip(ref NativeMethods.NOTIFYICONDATA data, string tip) => data.szTip = tip;

    /// <summary>Truncates a string to at most the given length.</summary>
    /// <param name="s">The string to truncate.</param>
    /// <param name="max">The maximum length to keep.</param>
    static string Trim(string s, int max) => s.Length <= max ? s : s[..max];

    /// <summary>Loads the tray icon from the running executable, falling back to the default application icon.</summary>
    IntPtr LoadAppIcon()
    {
        try
        {
            var exe = Environment.ProcessPath;
            if (!string.IsNullOrEmpty(exe))
            {
                var icon = NativeMethods.ExtractIcon(IntPtr.Zero, exe, 0);
                if (icon != IntPtr.Zero)
                    return icon;
            }
        }
        catch { }
        return NativeMethods.LoadIcon(IntPtr.Zero, (IntPtr)32512 /* IDI_APPLICATION */);
    }
}
