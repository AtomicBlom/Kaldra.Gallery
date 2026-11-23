using HelloAudio;

using BinaryVibrance.Sirrow.Sdl;
using BinaryVibrance.Sirrow.Web;
using BinaryVibrance.Kaldra.Core.Hosting;

// The desktop and browser entry point. On Android, Android starts the app through MainActivity instead.
// The platform's audio output first, which the app opens as it loads (AudioOutput.Open)
if (OperatingSystem.IsBrowser())
	await WebAudio.RegisterAsync();
else
	SdlAudio.Register();
KaldraApplication.Run(new AudioApp(), args);
