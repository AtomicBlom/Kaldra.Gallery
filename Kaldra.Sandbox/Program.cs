using BinaryVibrance.Kaldra.Core.Hosting;

namespace Kaldra.Sandbox;

// The sandbox entry point: hand the demo-gallery app to the reusable platform host, which owns the window, engine
// bring-up, input pump, and frame loop (backend + presentation flags are the host's HostOptions; the app's own
// --demo/--dump-graph/--inspect/--frustum/--no-hud arrive through AppArgs). One line, shared by every platform —
// SandboxApp is the gallery itself.
internal static class Program
{
	private static void Main(string[] args) => KaldraApplication.Run(new SandboxApp(), args);
}
