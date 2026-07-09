using Microsoft.Maui.Controls.Xaml;

namespace DropHarvester.Localization;

/// <summary>
/// XAML markup extension for localized text: <c>Text="{loc:Tr Status_Account}"</c>. Returns a one-way
/// binding to <see cref="LocalizationManager"/>'s indexer so the text re-translates live when the
/// language changes.
/// </summary>
[ContentProperty(nameof(Key))]
public sealed class TrExtension : IMarkupExtension<BindingBase>
{
    /// <summary>The string key to translate (the content of the extension, e.g. <c>{loc:Tr Status_Account}</c>).</summary>
    public string Key { get; set; } = "";

    /// <summary>Builds the binding to the localization manager's indexer for <see cref="Key"/>.</summary>
    /// <param name="serviceProvider">XAML service provider (unused).</param>
    public BindingBase ProvideValue(IServiceProvider serviceProvider) => new Binding
    {
        Mode = BindingMode.OneWay,
        Path = $"[{Key}]",
        Source = LocalizationManager.Instance,
    };

    object IMarkupExtension.ProvideValue(IServiceProvider serviceProvider) => ProvideValue(serviceProvider);
}
