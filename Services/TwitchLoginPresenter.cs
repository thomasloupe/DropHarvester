using DropHarvester.Views;

namespace DropHarvester.Services;

/// <summary>Opens the Twitch sign-in popup. Kept as a service so the Status view model can run a login
/// without depending on the page directly.</summary>
public interface ITwitchLoginPresenter
{
    /// <summary>Show the sign-in popup as a modal over the current page and wait for it to finish.</summary>
    /// <returns>The access token from the sign-in, or null if the popup closed without one.</returns>
    Task<string?> SignInAsync();
}

/// <summary>Default presenter: pushes a fresh <see cref="LoginPage"/> modally and awaits its result.</summary>
public sealed class TwitchLoginPresenter : ITwitchLoginPresenter
{
    /// <inheritdoc/>
    public async Task<string?> SignInAsync()
    {
        var nav = Shell.Current?.Navigation
                  ?? Application.Current?.Windows.FirstOrDefault()?.Page?.Navigation;
        if (nav is null)
            return null;
        var page = new LoginPage();
        await nav.PushModalAsync(page);
        return await page.Result;
    }
}
