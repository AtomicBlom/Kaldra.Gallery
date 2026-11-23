using System.Numerics;

using BinaryVibrance.Kaldra.Concepts;
using BinaryVibrance.Kaldra.Core.Hosting;

namespace Kaldra.Gallery.Demos;

/// <summary>
/// The shared directional "sun" the single-shadow demos cast from: a fixed light direction and an orthographic
/// light camera framing the reference scene. It's app data — the engine carries no light or sun concept — so it
/// lives with the demos. Each shadow demo builds a <see cref="Camera"/> from <see cref="View"/> and publishes its
/// <see cref="Camera.ViewProjection"/> into the constants buffer the depth pass reads (and the lighting pass reads
/// the same matrix to project world positions into the shadow map).
/// </summary>
internal static class DemoSun
{
	/// <summary>The sun's direction (pointing toward the sun), matching the sandbox's key light in the lit shaders.</summary>
	public static readonly Vector3 Direction = Vector3.Normalize(new Vector3(0.45f, 0.55f, -0.6f));

	/// <summary>A fresh light <see cref="Camera"/> framing the reference scene from the sun, clip-space-corrected via <paramref name="clip"/>.</summary>
	public static Camera View(IEngineClipSpace clip) =>
		new Camera(clip)
			.LookAt(Direction * 7f, Vector3.Zero)
			.Orthographic(9f, 9f, 0.1f, 14f);
}
