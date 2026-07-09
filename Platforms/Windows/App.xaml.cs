using System.Diagnostics;
using System.Runtime.InteropServices;
using Microsoft.UI.Xaml;

namespace DropHarvester.WinUI;

/// <summary>
/// Provides application-specific behavior to supplement the default Application class.
/// </summary>
public partial class App : MauiWinUIApplication
{
    // Kept alive for the process lifetime so the single-instance lock persists.
    static Mutex? _instanceMutex;

    /// <summary>Enforces the single-instance lock, then initializes the WinUI application component.</summary>
    public App()
    {
        EnforceSingleInstance();
        this.InitializeComponent();
    }

    /// <summary>Builds the shared MAUI application hosted by this WinUI process.</summary>
    protected override MauiApp CreateMauiApp() => MauiProgram.CreateMauiApp();

    /// <summary>Acquires a named mutex; if another instance already owns it, activates that instance and exits this one.</summary>
    static void EnforceSingleInstance()
    {
        _instanceMutex = new Mutex(initiallyOwned: true, @"Local\DropHarvester_SingleInstance", out var createdNew);
        if (createdNew)
            return;

        ActivateExistingInstance();
        Environment.Exit(0);
    }

    /// <summary>Finds the other DropHarvester process and restores and foregrounds its main window.</summary>
    static void ActivateExistingInstance()
    {
        try
        {
            var me = Process.GetCurrentProcess();
            foreach (var p in Process.GetProcessesByName(me.ProcessName))
            {
                if (p.Id == me.Id || p.MainWindowHandle == IntPtr.Zero)
                    continue;
                ShowWindow(p.MainWindowHandle, 9 /* SW_RESTORE */);
                SetForegroundWindow(p.MainWindowHandle);
                break;
            }
        }
        catch
        {
            // Best-effort; we exit regardless.
        }
    }

    /// <summary>Brings the given window to the foreground.</summary>
    [DllImport("user32.dll")] static extern bool SetForegroundWindow(IntPtr hWnd);
    /// <summary>Sets the given window's show state (restore, hide, etc.).</summary>
    [DllImport("user32.dll")] static extern bool ShowWindow(IntPtr hWnd, int nCmdShow);
}
