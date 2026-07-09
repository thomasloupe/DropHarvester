using System.Runtime.InteropServices;

namespace DropHarvester.Platforms.Windows;

/// <summary>Win32 interop for the system-tray icon, window subclassing, and popup menu.</summary>
internal static class NativeMethods
{
    /// <summary>Describes a system-tray icon for Shell_NotifyIcon.</summary>
    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    public struct NOTIFYICONDATA
    {
        public int cbSize;
        public IntPtr hWnd;
        public uint uID;
        public uint uFlags;
        public uint uCallbackMessage;
        public IntPtr hIcon;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 128)] public string szTip;
        public uint dwState;
        public uint dwStateMask;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 256)] public string szInfo;
        public uint uVersionOrTimeout;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 64)] public string szInfoTitle;
        public uint dwInfoFlags;
        public Guid guidItem;
        public IntPtr hBalloonIcon;
    }

    /// <summary>A screen coordinate pair.</summary>
    [StructLayout(LayoutKind.Sequential)]
    public struct POINT
    {
        public int X;
        public int Y;
    }

    /// <summary>Window subclass procedure invoked for messages sent to a subclassed window.</summary>
    public delegate IntPtr SUBCLASSPROC(
        IntPtr hWnd, uint uMsg, IntPtr wParam, IntPtr lParam, nuint uIdSubclass, nuint dwRefData);

    /// <summary>Adds, modifies, or deletes the app's system-tray icon.</summary>
    [DllImport("shell32.dll", CharSet = CharSet.Unicode)]
    public static extern bool Shell_NotifyIcon(uint dwMessage, ref NOTIFYICONDATA lpData);

    /// <summary>Installs a subclass window procedure for the given window.</summary>
    [DllImport("comctl32.dll", SetLastError = true)]
    public static extern bool SetWindowSubclass(IntPtr hWnd, SUBCLASSPROC pfnSubclass, nuint uIdSubclass, nuint dwRefData);

    /// <summary>Calls the next handler in a window's subclass chain.</summary>
    [DllImport("comctl32.dll")]
    public static extern IntPtr DefSubclassProc(IntPtr hWnd, uint uMsg, IntPtr wParam, IntPtr lParam);

    /// <summary>Creates an empty popup menu.</summary>
    [DllImport("user32.dll")]
    public static extern IntPtr CreatePopupMenu();

    /// <summary>Appends an item to the given menu.</summary>
    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    public static extern bool AppendMenu(IntPtr hMenu, uint uFlags, int uIDNewItem, string lpNewItem);

    /// <summary>Displays a popup menu and returns the chosen command id.</summary>
    [DllImport("user32.dll")]
    public static extern uint TrackPopupMenu(IntPtr hMenu, uint uFlags, int x, int y, int nReserved, IntPtr hWnd, IntPtr prcRect);

    /// <summary>Destroys the given menu and frees its resources.</summary>
    [DllImport("user32.dll")]
    public static extern bool DestroyMenu(IntPtr hMenu);

    /// <summary>Gets the cursor position in screen coordinates.</summary>
    [DllImport("user32.dll")]
    public static extern bool GetCursorPos(out POINT lpPoint);

    /// <summary>Brings the given window to the foreground.</summary>
    [DllImport("user32.dll")]
    public static extern bool SetForegroundWindow(IntPtr hWnd);

    /// <summary>Sets the given window's show state (restore, hide, etc.).</summary>
    [DllImport("user32.dll")]
    public static extern bool ShowWindow(IntPtr hWnd, int nCmdShow);

    /// <summary>Loads a standard system icon resource.</summary>
    [DllImport("user32.dll")]
    public static extern IntPtr LoadIcon(IntPtr hInstance, IntPtr lpIconName);

    /// <summary>Extracts an icon from an executable or icon file.</summary>
    [DllImport("shell32.dll", CharSet = CharSet.Unicode)]
    public static extern IntPtr ExtractIcon(IntPtr hInst, string lpszExeFileName, int nIconIndex);
}
