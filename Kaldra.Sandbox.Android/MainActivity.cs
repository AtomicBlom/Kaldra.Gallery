using Android.App;
using Android.Content.PM;

using BinaryVibrance.Kaldra.Core.Hosting;
using Kaldra.Gallery.Hosting;
using BinaryVibrance.Kaldra.Hosting.Android;

namespace Kaldra.Sandbox.Android;

/// <summary>
/// The gallery's Android entry point: runs <see cref="GalleryApp"/>, reading its models and textures from the APK's
/// assets.
/// </summary>
[Activity(
	Label = "Kaldra Gallery",
	MainLauncher = true,
	ConfigurationChanges = KaldraActivity.HandledConfigChanges,
	Theme = KaldraActivity.FullscreenTheme,
	ScreenOrientation = ScreenOrientation.FullUser)]
public sealed class MainActivity : KaldraActivity
{
	protected override KaldraApplication CreateApplication() => new GalleryApp(ReadAsset);

	// Called from the demos' loading threads as well as this one; Android's asset manager may be read from any thread.
	private byte[] ReadAsset(string name)
	{
		using var stream = Assets!.Open(name);
		using var bytes = new MemoryStream();
		stream.CopyTo(bytes);
		return bytes.ToArray();
	}
}
