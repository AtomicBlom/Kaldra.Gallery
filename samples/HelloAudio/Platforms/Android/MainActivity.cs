using Android.App;
using Android.Content.PM;

using Loom.Audio.Android;
using Loom.Core.Hosting;
using Loom.Hosting.Android;

namespace HelloAudio;

/// <summary>The Android entry point: Android starts the app by creating this activity, which runs <see cref="AudioApp"/>.</summary>
[Activity(
	Label = "Loom Hello Audio",
	MainLauncher = true,
	ConfigurationChanges = LoomActivity.HandledConfigChanges,
	Theme = LoomActivity.FullscreenTheme,
	ScreenOrientation = ScreenOrientation.FullUser)]
public sealed class MainActivity : LoomActivity
{
	protected override LoomApplication CreateApplication()
	{
		AndroidAudio.Register(); // the output the app opens as it loads (AudioOutput.Open)
		return new AudioApp();
	}
}
