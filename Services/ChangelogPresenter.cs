using DropHarvester.Views;

namespace DropHarvester.Services;

/// <summary>Opens the "What's changed" popup. Kept as a service so both the Status and Settings view models
/// can trigger it without depending on the page directly.</summary>
public interface IChangelogPresenter
{
    /// <summary>Show the changelog popup as a modal over the current page.</summary>
    Task ShowAsync();
}

/// <summary>Default presenter: resolves a fresh <see cref="ChangelogPage"/> from DI and pushes it modally.</summary>
public sealed class ChangelogPresenter : IChangelogPresenter
{
    readonly IServiceProvider _services;

    /// <summary>Creates the presenter with the DI container used to build the popup page.</summary>
    /// <param name="services">The application service provider.</param>
    public ChangelogPresenter(IServiceProvider services) => _services = services;

    /// <inheritdoc/>
    public async Task ShowAsync()
    {
        if (_services.GetService(typeof(ChangelogPage)) is not ChangelogPage page)
            return;
        var nav = Shell.Current?.Navigation
                  ?? Application.Current?.Windows.FirstOrDefault()?.Page?.Navigation;
        if (nav is not null)
            await nav.PushModalAsync(page);
    }
}
