using System.Runtime.InteropServices;
using DropHarvester.Services;
using Foundation;

namespace DropHarvester.Platforms.MacCatalyst;

/// <summary>
/// macOS menu-bar presence via an <c>NSStatusItem</c>. Mac Catalyst can't reference AppKit at compile
/// time, so the status item, its menu, and the click actions are driven through the Objective-C runtime
/// (<c>objc_msgSend</c>). The item shows a short "DH" title and a menu with a status line, Open, and
/// Quit; harvesting keeps running while the window is closed. Every call is defensive - any interop
/// failure degrades to a no-op so the menu bar can never crash the app or interrupt harvesting.
/// </summary>
public sealed class MacTrayService : ITrayService
{
    const string Objc = "/usr/lib/libobjc.dylib";
    const double NSVariableStatusItemLength = -1.0;

    [DllImport(Objc)] static extern IntPtr objc_getClass(string name);
    [DllImport(Objc)] static extern IntPtr sel_registerName(string name);
    [DllImport(Objc, EntryPoint = "objc_msgSend")] static extern IntPtr IntPtr_msgSend(IntPtr receiver, IntPtr sel);
    [DllImport(Objc, EntryPoint = "objc_msgSend")] static extern IntPtr IntPtr_msgSend_IntPtr(IntPtr receiver, IntPtr sel, IntPtr a);
    [DllImport(Objc, EntryPoint = "objc_msgSend")] static extern IntPtr IntPtr_msgSend_Double(IntPtr receiver, IntPtr sel, double a);
    [DllImport(Objc, EntryPoint = "objc_msgSend")] static extern IntPtr IntPtr_msgSend_III(IntPtr receiver, IntPtr sel, IntPtr a, IntPtr b, IntPtr c);
    [DllImport(Objc, EntryPoint = "objc_msgSend")] static extern void void_msgSend_IntPtr(IntPtr receiver, IntPtr sel, IntPtr a);
    [DllImport(Objc, EntryPoint = "objc_msgSend")] static extern void void_msgSend_Bool(IntPtr receiver, IntPtr sel, [MarshalAs(UnmanagedType.I1)] bool a);

    TrayActionTarget? _target; // kept rooted so the Objective-C action target stays alive
    IntPtr _statusItem;
    IntPtr _statusMenuItem;    // the disabled status line updated from SetStatus
    bool _created;

    /// <summary>A real menu-bar item is attempted on macOS.</summary>
    public bool IsSupported => true;

    /// <summary>Creates the menu-bar item on the main thread (the window itself isn't needed).</summary>
    /// <param name="window">The MAUI application window.</param>
    public void AttachWindow(Window window)
    {
        // Defer to the next main-loop turn so NSApplication is fully up before we touch AppKit.
        MainThread.BeginInvokeOnMainThread(CreateStatusItem);
    }

    /// <summary>Builds the NSStatusItem and its menu through the Objective-C runtime; no-ops on any failure.</summary>
    void CreateStatusItem()
    {
        if (_created)
            return;
        try
        {
            _target = new TrayActionTarget(ShowWindow, QuitApp);

            var statusBar = IntPtr_msgSend(objc_getClass("NSStatusBar"), sel_registerName("systemStatusBar"));
            if (statusBar == IntPtr.Zero)
                return;

            _statusItem = IntPtr_msgSend_Double(statusBar, sel_registerName("statusItemWithLength:"), NSVariableStatusItemLength);
            if (_statusItem == IntPtr.Zero)
                return;
            IntPtr_msgSend(_statusItem, sel_registerName("retain")); // the status item is autoreleased - keep it

            // Short button title; the live status text lives in the menu so the menu bar stays compact.
            var button = IntPtr_msgSend(_statusItem, sel_registerName("button"));
            if (button != IntPtr.Zero)
                SetTitle(button, "DH");

            var menu = IntPtr_msgSend(IntPtr_msgSend(objc_getClass("NSMenu"), sel_registerName("alloc")), sel_registerName("init"));

            _statusMenuItem = AddItem(menu, "DropHarvester", action: null, enabled: false);
            AddSeparator(menu);
            AddItem(menu, "Open DropHarvester", action: "openClicked:", enabled: true);
            AddItem(menu, "Quit", action: "quitClicked:", enabled: true);

            void_msgSend_IntPtr(_statusItem, sel_registerName("setMenu:"), menu);
            _created = true;
        }
        catch
        {
            // Any interop failure -> silently no-op (the menu bar item simply won't appear).
        }
    }

    /// <summary>Creates an NSMenuItem, optionally wiring its action to the shared target, and appends it.</summary>
    /// <param name="menu">The NSMenu handle to append to.</param>
    /// <param name="title">The item title.</param>
    /// <param name="action">The Objective-C action selector, or null for a plain (disabled) item.</param>
    /// <param name="enabled">Whether the item is enabled/clickable.</param>
    /// <returns>The created NSMenuItem handle.</returns>
    IntPtr AddItem(IntPtr menu, string title, string? action, bool enabled)
    {
        using var t = new NSString(title);
        using var empty = new NSString(string.Empty);
        var alloc = IntPtr_msgSend(objc_getClass("NSMenuItem"), sel_registerName("alloc"));
        var actionSel = action is null ? IntPtr.Zero : sel_registerName(action);
        var item = IntPtr_msgSend_III(alloc, sel_registerName("initWithTitle:action:keyEquivalent:"), t.Handle, actionSel, empty.Handle);
        if (action is not null && _target is not null)
            void_msgSend_IntPtr(item, sel_registerName("setTarget:"), _target.Handle);
        if (!enabled)
            void_msgSend_Bool(item, sel_registerName("setEnabled:"), false);
        void_msgSend_IntPtr(menu, sel_registerName("addItem:"), item);
        return item;
    }

    /// <summary>Appends a separator item to the menu.</summary>
    /// <param name="menu">The NSMenu handle.</param>
    void AddSeparator(IntPtr menu)
    {
        var sep = IntPtr_msgSend(objc_getClass("NSMenuItem"), sel_registerName("separatorItem"));
        void_msgSend_IntPtr(menu, sel_registerName("addItem:"), sep);
    }

    /// <summary>Sets an Objective-C object's <c>title</c> (via <c>setTitle:</c>) to the given string.</summary>
    /// <param name="obj">The NSButton / NSMenuItem handle.</param>
    /// <param name="title">The new title text.</param>
    static void SetTitle(IntPtr obj, string title)
    {
        using var s = new NSString(title);
        void_msgSend_IntPtr(obj, sel_registerName("setTitle:"), s.Handle);
    }

    /// <summary>Updates the menu's status line to the current harvesting status.</summary>
    /// <param name="status">The status text to display.</param>
    public void SetStatus(string status)
    {
        if (!_created || _statusMenuItem == IntPtr.Zero)
            return;
        void Update()
        {
            try { SetTitle(_statusMenuItem, string.IsNullOrWhiteSpace(status) ? "DropHarvester" : status); }
            catch { }
        }
        if (MainThread.IsMainThread) Update();
        else MainThread.BeginInvokeOnMainThread(Update);
    }

    /// <summary>Brings the app to the foreground (Open menu item).</summary>
    public void ShowWindow()
    {
        void Show()
        {
            try
            {
                var app = IntPtr_msgSend(objc_getClass("NSApplication"), sel_registerName("sharedApplication"));
                if (app != IntPtr.Zero)
                    void_msgSend_Bool(app, sel_registerName("activateIgnoringOtherApps:"), true);
            }
            catch { }
        }
        if (MainThread.IsMainThread) Show();
        else MainThread.BeginInvokeOnMainThread(Show);
    }

    /// <summary>Quits the application on the main thread (Quit menu item).</summary>
    void QuitApp()
    {
        MainThread.BeginInvokeOnMainThread(() =>
        {
            try { Microsoft.Maui.Controls.Application.Current?.Quit(); } catch { }
        });
    }

    /// <summary>Objective-C action target: routes the menu-item selectors to the managed callbacks.</summary>
    sealed class TrayActionTarget : NSObject
    {
        readonly Action _onOpen;
        readonly Action _onQuit;

        /// <summary>Captures the Open and Quit callbacks the menu items invoke.</summary>
        /// <param name="onOpen">Invoked when "Open" is chosen.</param>
        /// <param name="onQuit">Invoked when "Quit" is chosen.</param>
        public TrayActionTarget(Action onOpen, Action onQuit)
        {
            _onOpen = onOpen;
            _onQuit = onQuit;
        }

        /// <summary>Handles the Open menu item's action.</summary>
        /// <param name="sender">The sending menu item.</param>
        [Export("openClicked:")]
        void OpenClicked(NSObject sender) => _onOpen();

        /// <summary>Handles the Quit menu item's action.</summary>
        /// <param name="sender">The sending menu item.</param>
        [Export("quitClicked:")]
        void QuitClicked(NSObject sender) => _onQuit();
    }
}
