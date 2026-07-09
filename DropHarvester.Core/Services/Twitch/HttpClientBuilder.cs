using System.Net;
using DropHarvester.Services;

namespace DropHarvester.Services.Twitch;

/// <summary>
/// Builds HttpClientHandlers that honor the user's optional proxy setting (gzip decompression
/// on by default). Proxy changes take effect on restart.
/// </summary>
public static class HttpClientBuilder
{
    /// <summary>Builds an HttpClientHandler with gzip/deflate decompression and the configured proxy applied.</summary>
    /// <param name="settings">Settings store whose Proxy value is applied to the handler.</param>
    /// <returns>A handler ready to back an HttpClient.</returns>
    public static HttpClientHandler CreateHandler(ISettingsStore settings)
    {
        var handler = new HttpClientHandler
        {
            AutomaticDecompression = DecompressionMethods.GZip | DecompressionMethods.Deflate,
        };
        ApplyProxy(handler, settings.Settings.Proxy);
        return handler;
    }

    /// <summary>Parses a proxy address into a WebProxy, or null if blank or invalid.</summary>
    /// <param name="proxy">Proxy address string; blank or unparseable input yields null.</param>
    /// <returns>A WebProxy for the address, or null when none should be used.</returns>
    public static IWebProxy? BuildProxy(string? proxy)
    {
        if (string.IsNullOrWhiteSpace(proxy))
            return null;
        try { return new WebProxy(proxy.Trim()); }
        catch { return null; }
    }

    /// <summary>Applies the parsed proxy to the handler, enabling proxy use when one is present.</summary>
    /// <param name="handler">Handler to configure.</param>
    /// <param name="proxy">Proxy address string; when blank or invalid the handler is left unchanged.</param>
    static void ApplyProxy(HttpClientHandler handler, string? proxy)
    {
        var wp = BuildProxy(proxy);
        if (wp is not null)
        {
            handler.Proxy = wp;
            handler.UseProxy = true;
        }
    }
}
