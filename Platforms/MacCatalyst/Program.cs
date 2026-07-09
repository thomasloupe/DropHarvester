using ObjCRuntime;
using UIKit;

namespace DropHarvester;

/// <summary>Native entry point for the Mac Catalyst app.</summary>
public class Program
{
	/// <summary>Starts the UIKit application using the AppDelegate.</summary>
	/// <param name="args">Command-line arguments passed to UIApplication.</param>
	static void Main(string[] args)
	{
		UIApplication.Main(args, null, typeof(AppDelegate));
	}
}
