using Android.App;
using Android.Content.PM;

using Loom.Core.Hosting;
using Loom.Gallery.Hosting;
using Loom.Hosting.Android;

namespace Loom.Sandbox.Android;

/// <summary>
/// The gallery's Android entry point: runs <see cref="GalleryApp"/>, reading its models and textures from the APK's
/// assets.
/// </summary>
[Activity(
	Label = "Loom Gallery",
	MainLauncher = true,
	ConfigurationChanges = LoomActivity.HandledConfigChanges,
	Theme = LoomActivity.FullscreenTheme,
	ScreenOrientation = ScreenOrientation.FullUser)]
public sealed class MainActivity : LoomActivity
{
	protected override LoomApplication CreateApplication() => new GalleryApp(ReadAsset);

	// Called from the demos' loading threads as well as this one; Android's asset manager may be read from any thread.
	private byte[] ReadAsset(string name)
	{
		using var stream = Assets!.Open(name);
		using var bytes = new MemoryStream();
		stream.CopyTo(bytes);
		return bytes.ToArray();
	}
}
