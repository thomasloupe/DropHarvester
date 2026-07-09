using DropHarvester.Localization;
using DropHarvester.Views;

namespace DropHarvester;

/// <summary>
/// Code-defined shell so the tab pages are resolved from DI (and therefore share singletons
/// like the harvester orchestrator and its view models). Built as a tab bar, dark themed.
/// </summary>
public class AppShell : Shell
{
    /// <summary>Builds the dark-themed tab bar, resolving each tab page from DI.</summary>
    /// <param name="statusPage">The Status tab page.</param>
    /// <param name="campaignsPage">The Inventory tab page.</param>
    /// <param name="channelsPage">The Channels tab page.</param>
    /// <param name="statsPage">The Stats tab page.</param>
    /// <param name="settingsPage">The Settings tab page.</param>
    /// <param name="logPage">The Log tab page.</param>
    public AppShell(
        StatusPage statusPage,
        CampaignsPage campaignsPage,
        ChannelsPage channelsPage,
        StatsPage statsPage,
        SettingsPage settingsPage,
        LogPage logPage)
    {
        Title = "DropHarvester";

        var res = Application.Current!.Resources;
        var bg = (Color)res["DhBg"];
        var surface = (Color)res["DhSurface"];
        var accent = (Color)res["DhAccent"];
        var text = (Color)res["DhText"];
        var muted = (Color)res["DhMuted"];

        BackgroundColor = bg;
        Shell.SetTabBarBackgroundColor(this, surface);
        Shell.SetTabBarForegroundColor(this, accent);
        Shell.SetTabBarTitleColor(this, accent);
        Shell.SetTabBarUnselectedColor(this, muted);
        Shell.SetBackgroundColor(this, bg);
        Shell.SetForegroundColor(this, text);
        Shell.SetTitleColor(this, text);
        FlyoutBehavior = FlyoutBehavior.Disabled;

        var tabs = new TabBar();
        AddTab(tabs, statusPage, "status", "Tab_Status");
        AddTab(tabs, campaignsPage, "inventory", "Tab_Inventory");
        AddTab(tabs, channelsPage, "channels", "Tab_Channels");
        AddTab(tabs, statsPage, "stats", "Tab_Stats");
        AddTab(tabs, settingsPage, "settings", "Tab_Settings");
        AddTab(tabs, logPage, "log", "Tab_Log");

        Items.Add(tabs);
    }

    /// <summary>Adds a tab whose title is bound to the localized string for <paramref name="titleKey"/>, so
    /// it re-translates live when the language changes.</summary>
    /// <param name="tabs">The tab bar to add to.</param>
    /// <param name="page">The tab's content page.</param>
    /// <param name="route">The tab's shell route.</param>
    /// <param name="titleKey">The localization key for the tab title.</param>
    static void AddTab(TabBar tabs, Page page, string route, string titleKey)
    {
        var content = new ShellContent { Content = page, Route = route };
        content.SetBinding(ShellContent.TitleProperty, new Binding($"[{titleKey}]", source: LocalizationManager.Instance));
        tabs.Items.Add(content);
    }
}
