using System.Text;
using System.Text.Json;
using DropHarvester.Localization;
using DropHarvester.Models;

namespace DropHarvester.Services;

/// <summary>Posts alert messages to a user-configured Discord/Slack/generic webhook.</summary>
public interface IWebhookNotifier
{
    /// <summary>Send a formatted alert if webhooks are enabled and a URL is set.</summary>
    Task SendAsync(string title, string message, CancellationToken ct = default);

    /// <summary>Send a test message regardless of the enabled toggle; returns a status string.</summary>
    Task<string> SendTestAsync(CancellationToken ct = default);
}

/// <summary>HttpClient-backed implementation of <see cref="IWebhookNotifier"/>.</summary>
public sealed class WebhookNotifier : IWebhookNotifier, IDisposable
{
    readonly ISettingsStore _settings;
    readonly HttpClient _http = new();

    /// <summary>Creates the notifier reading its webhook configuration from the settings store.</summary>
    /// <param name="settings">Settings store providing the webhook toggle, URL, and kind.</param>
    public WebhookNotifier(ISettingsStore settings) => _settings = settings;

    AppSettings S => _settings.Settings;

    /// <summary>Posts the alert when webhooks are enabled and a URL is set; swallows any send failure.</summary>
    /// <param name="title">Alert title/heading.</param>
    /// <param name="message">Alert body text.</param>
    /// <param name="ct">Token to cancel the request.</param>
    public async Task SendAsync(string title, string message, CancellationToken ct = default)
    {
        if (!S.WebhookEnabled || string.IsNullOrWhiteSpace(S.WebhookUrl))
            return;
        try { await PostAsync(S.WebhookUrl!, S.WebhookKind, title, message, ct).ConfigureAwait(false); }
        catch { }
    }

    /// <summary>Sends a fixed test message regardless of the enabled toggle and reports the result.</summary>
    /// <param name="ct">Token to cancel the request.</param>
    /// <returns>A human-readable status string describing success or the failure.</returns>
    public async Task<string> SendTestAsync(CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(S.WebhookUrl))
            return Loc.T("Settings_WebhookEnterUrlFirst");
        try
        {
            var resp = await PostAsync(S.WebhookUrl!, S.WebhookKind, "DropHarvester", "Test message - webhook is working.", ct)
                .ConfigureAwait(false);
            return resp ? Loc.T("Settings_WebhookTestSent") : Loc.T("Settings_WebhookReturnedError");
        }
        catch (Exception ex)
        {
            return Loc.T("Settings_WebhookFailed", ex.Message);
        }
    }

    /// <summary>Serializes the alert into the kind-specific JSON payload and POSTs it to the webhook URL.</summary>
    /// <param name="url">Webhook endpoint to post to.</param>
    /// <param name="kind">Payload format to use (Discord, Slack, or generic).</param>
    /// <param name="title">Alert title/heading.</param>
    /// <param name="message">Alert body text.</param>
    /// <param name="ct">Token to cancel the request.</param>
    /// <returns>True if the response status indicates success.</returns>
    async Task<bool> PostAsync(string url, WebhookKind kind, string title, string message, CancellationToken ct)
    {
        var body = kind switch
        {
            WebhookKind.Discord => JsonSerializer.Serialize(new { content = $"**{title}**\n{message}" }),
            WebhookKind.Slack => JsonSerializer.Serialize(new { text = $"*{title}*\n{message}" }),
            _ => JsonSerializer.Serialize(new { title, message }),
        };
        using var content = new StringContent(body, Encoding.UTF8, "application/json");
        using var resp = await _http.PostAsync(url, content, ct).ConfigureAwait(false);
        return resp.IsSuccessStatusCode;
    }

    /// <summary>Disposes the underlying HttpClient.</summary>
    public void Dispose() => _http.Dispose();
}
