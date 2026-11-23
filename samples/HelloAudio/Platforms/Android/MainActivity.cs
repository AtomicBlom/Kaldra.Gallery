using Android.App;
using Android.Content.PM;

using BinaryVibrance.Sirrow.Android;
using BinaryVibrance.Kaldra.Core.Hosting;
using BinaryVibrance.Kaldra.Hosting.Android;

namespace HelloAudio;

/// <summary>The Android entry point: Android starts the app by creating this activity, which runs <see cref="AudioApp"/>.</summary>
[Activity(
	Label = "Kaldra Hello Audio",
	MainLauncher = true,
	ConfigurationChanges = KaldraActivity.HandledConfigChanges,
	Theme = KaldraActivity.FullscreenTheme,
	ScreenOrientation = ScreenOrientation.FullUser)]
public sealed class MainActivity : KaldraActivity
{
	protected override KaldraApplication CreateApplication()
	{
		AndroidAudio.Register(); // the output the app opens as it loads (AudioOutput.Open)
		return new AudioApp();
	}
}
