using Foundation;

namespace DropHarvester;

/// <summary>Mac Catalyst application delegate that builds the shared MAUI app.</summary>
[Register("AppDelegate")]
public class AppDelegate : MauiUIApplicationDelegate
{
	/// <summary>Builds the shared MAUI application for the Mac Catalyst host.</summary>
	protected override MauiApp CreateMauiApp() => MauiProgram.CreateMauiApp();
}
