using System.Numerics;

using Loom.Concepts;

namespace Loom.Sandbox.Demos;

/// <summary>
/// The shared orbiting viewpoint the 3D demos animate — the camera every demo that doesn't override
/// <see cref="IDemo.GetCamera"/> is rendered from. Both hosts drive it through this one function so a browser capture
/// at the frozen time lands on the identical viewpoint as the desktop Vulkan reference, making the two directly
/// pixel-comparable.
/// </summary>
internal static class DemoCamera
{
	/// <summary>
	/// Point <paramref name="camera"/> at the origin from the shared orbit at time <paramref name="time"/>: a 2.5-unit
	/// radius circle at eye-height 1.6, a 60° vertical field of view.
	/// </summary>
	public static void Orbit(Camera camera, float time, float aspect)
	{
		var eye = new Vector3(MathF.Sin(time) * 2.5f, 1.6f, MathF.Cos(time) * 2.5f);
		camera.Perspective(MathF.PI / 3f, aspect, 0.1f, 100f).LookAt(eye, Vector3.Zero);
	}
}
