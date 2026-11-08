using DropHarvester.Models.Twitch;
using DropHarvester.Services.Twitch;

namespace DropHarvester.Views;

/// <summary>Code-behind for the Twitch sign-in popup: hosts Twitch's own sign-in page in a web view and
/// completes as soon as the page redirects to twitch.tv carrying the access token, or when it is closed.
/// Browser data is cleared first so every sign-in starts signed out (logging out and back in can pick a
/// different account).</summary>
public partial class LoginPage : ContentPage
{
#if MACCATALYST
    // WKWebView's default user agent omits the Safari version, which Twitch treats as an unsupported browser.
    const string SafariUserAgent =
        "Mozilla/5.0 (Macintosh; Intel Mac OS X 10_15_7) AppleWebKit/605.1.15 (KHTML, like Gecko) Version/26.0 Safari/605.1.15";
#endif

    readonly TaskCompletionSource<string?> _result = new(TaskCreationOptions.RunContinuationsAsynchronously);
    IDispatcherTimer? _poll;
    bool _started;
    bool _checking;

    /// <summary>The access token once sign-in finishes, or null if the popup closed without one.</summary>
    public Task<string?> Result => _result.Task;

    /// <summary>Builds the popup and hooks the web view's navigation events; the sign-in page loads once
    /// the web view's platform handler exists.</summary>
    public LoginPage()
    {
        InitializeComponent();
#if MACCATALYST
        LoginWebView.UserAgent = SafariUserAgent;
#endif
        LoginWebView.Navigating += OnNavigating;
        LoginWebView.Navigated += OnNavigated;
        LoginWebView.HandlerChanged += OnWebViewHandlerChanged;
    }

    /// <summary>Once the platform web view exists: clear its browser data, load the sign-in page, and start
    /// polling the address (the sign-in page can reach the redirect without a navigation event).</summary>
    /// <param name="sender">The web view.</param>
    /// <param name="e">Unused event args.</param>
    async void OnWebViewHandlerChanged(object? sender, EventArgs e)
    {
        if (_started || LoginWebView.Handler is null)
            return;
        _started = true;

        await ClearBrowserDataAsync();
        LoginWebView.Source = TwitchConstants.BrowserLoginUrl;

        _poll = Dispatcher.CreateTimer();
        _poll.Interval = TimeSpan.FromMilliseconds(500);
        _poll.Tick += OnPollTick;
        _poll.Start();
    }

    /// <summary>Deletes the web view's cookies and site data so the sign-in page starts signed out.
    /// Best-effort: a failure just means Twitch may remember the last account.</summary>
    async Task ClearBrowserDataAsync()
    {
        try
        {
#if WINDOWS
            if (LoginWebView.Handler?.PlatformView is Microsoft.UI.Xaml.Controls.WebView2 wv)
            {
                await wv.EnsureCoreWebView2Async();
                wv.CoreWebView2.CookieManager.DeleteAllCookies();
            }
#elif MACCATALYST
            await WebKit.WKWebsiteDataStore.DefaultDataStore.RemoveDataOfTypesAsync(
                WebKit.WKWebsiteDataStore.AllWebsiteDataTypes, Foundation.NSDate.DistantPast);
#else
            await Task.CompletedTask;
#endif
        }
        catch
        {
            // ignore; the sign-in still works, it just may start signed in
        }
    }

    /// <summary>Catches the redirect before twitch.tv loads: take the token and skip loading the page.</summary>
    /// <param name="sender">The web view.</param>
    /// <param name="e">The navigation being started.</param>
    void OnNavigating(object? sender, WebNavigatingEventArgs e)
    {
        if (TwitchAuthService.TokenFromRedirectUrl(e.Url) is { } token)
        {
            e.Cancel = true;
            _ = FinishAsync(token);
        }
    }

    /// <summary>Takes the token from a completed navigation to the redirect.</summary>
    /// <param name="sender">The web view.</param>
    /// <param name="e">The finished navigation.</param>
    void OnNavigated(object? sender, WebNavigatedEventArgs e)
    {
        if (TwitchAuthService.TokenFromRedirectUrl(e.Url) is { } token)
            _ = FinishAsync(token);
    }

    /// <summary>Reads the page's current address and finishes when it is the redirect carrying the token.</summary>
    /// <param name="sender">The poll timer.</param>
    /// <param name="e">Unused event args.</param>
    async void OnPollTick(object? sender, EventArgs e)
    {
        if (_checking || _result.Task.IsCompleted)
            return;
        _checking = true;
        try
        {
            var href = await LoginWebView.EvaluateJavaScriptAsync("window.location.href");
            if (TwitchAuthService.TokenFromRedirectUrl(href?.Trim('"')) is { } token)
                await FinishAsync(token);
        }
        catch
        {
            // the page is mid-navigation; try again on the next tick
        }
        finally
        {
            _checking = false;
        }
    }

    /// <summary>Closes the popup without a token when the X button is clicked.</summary>
    /// <param name="sender">The close button.</param>
    /// <param name="e">The click event args.</param>
    async void OnCloseClicked(object? sender, EventArgs e) => await FinishAsync(null);

    /// <summary>Completes the sign-in once (later calls are ignored), stops polling, and closes the popup.</summary>
    /// <param name="token">The access token, or null when the user closed the popup.</param>
    async Task FinishAsync(string? token)
    {
        if (!_result.TrySetResult(token))
            return;
        _poll?.Stop();
        if (Navigation.ModalStack.LastOrDefault() == this)
            await Navigation.PopModalAsync();
    }

    /// <summary>Ends the sign-in without a token if the popup goes away any other way.</summary>
    protected override void OnDisappearing()
    {
        base.OnDisappearing();
        _poll?.Stop();
        _result.TrySetResult(null);
    }
}
