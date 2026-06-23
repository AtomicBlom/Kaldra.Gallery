using System.Numerics;

using Loom.Assets;
using Loom.Core.Hosting;
using Loom.Core.Materials;

namespace Loom.Sandbox.Demos;

/// <summary>
/// A procedurally-built sprite-sheet animation strip for <see cref="SpriteAnimationDemo"/>: an
/// 8-frame horizontal strip of a disc with a bright wedge that rotates 45° per frame, so cycling the frames
/// reads as a spinning marker. Each frame also carries the same spherical tangent-space normal map as the ball
/// atlas, so the disc still lights with 3D relief under the compose pass — the animation rides on top of the
/// 2D deferred path unchanged. Row-major frame order matches <see cref="SpriteSheet.Uv"/>; the bytes are
/// deterministic (safe for a cross-backend golden).
/// </summary>
internal static class SpinnerAtlas
{
	public const int Frames = 8;

	/// <summary>Build the strip as an albedo (sRGB) + normal (linear) material, plus its <paramref name="sheet"/> (8×1).</summary>
	public static Material CreateMaterial(IEngine engine, out SpriteSheet sheet)
	{
		var (albedo, normal) = BuildPixels(out var width, out var height);
		sheet = new SpriteSheet(Frames, 1, width, height, insetTexels: 0.5f);
		return engine.CreateMaterial(
			engine.CreateTexture(TextureData.FromRgba8(width, height, albedo, TextureFormat.Rgba8Srgb, generateMips: false)),
			engine.CreateTexture(TextureData.FromRgba8(width, height, normal, TextureFormat.Rgba8Unorm, generateMips: false)));
	}

	private static (byte[] Albedo, byte[] Normal) BuildPixels(out int width, out int height)
	{
		const int cell = 32;
		width = Frames * cell;
		height = cell;
		var albedo = new byte[width * height * 4];
		var normal = new byte[width * height * 4];

		var baseColor = new Vector3(0.15f, 0.55f, 0.70f);    // teal disc
		var wedgeColor = new Vector3(1.00f, 0.92f, 0.55f);   // bright marker
		const float radius = cell * 0.45f;
		const float centre = cell * 0.5f;
		const float twoPi = MathF.PI * 2f;
		const float halfWedge = 0.0625f; // 45° wedge (in turns) — half-width 22.5°

		for (var f = 0; f < Frames; f++)
		{
			var wedgeCentre = f / (float)Frames; // the marker angle for this frame, in turns [0,1)
			for (var py = 0; py < cell; py++)
				for (var px = 0; px < cell; px++)
				{
					var dx = px + 0.5f - centre;
					var dy = py + 0.5f - centre;
					var nd = MathF.Sqrt(dx * dx + dy * dy) / radius;
					var gx = f * cell + px;
					var o = (py * width + gx) * 4;

					if (nd <= 1f)
					{
						var nz = MathF.Sqrt(MathF.Max(0f, 1f - nd * nd));
						var n = Vector3.Normalize(new Vector3(dx / radius, -dy / radius, nz)); // -dy: image-top → +Y

						// Angle of this pixel in turns [0,1); -dy so the wedge sweeps the same way +Y points.
						var turn = MathF.Atan2(-dy, dx) / twoPi;
						if (turn < 0f) turn += 1f;
						var d = MathF.Abs(turn - wedgeCentre);
						d = MathF.Min(d, 1f - d); // wrap-around angular distance
						var color = d < halfWedge ? wedgeColor : baseColor;

						albedo[o + 0] = ToByte(color.X);
						albedo[o + 1] = ToByte(color.Y);
						albedo[o + 2] = ToByte(color.Z);
						albedo[o + 3] = 255;
						normal[o + 0] = ToByte(n.X * 0.5f + 0.5f);
						normal[o + 1] = ToByte(n.Y * 0.5f + 0.5f);
						normal[o + 2] = ToByte(n.Z * 0.5f + 0.5f);
						normal[o + 3] = 255;
					}
					else
					{
						// Outside the disc: transparent (alpha-tested away); flat normal just in case.
						normal[o + 0] = 128;
						normal[o + 1] = 128;
						normal[o + 2] = 255;
						normal[o + 3] = 255;
					}
				}
		}

		return (albedo, normal);
	}

	private static byte ToByte(float v) => (byte)Math.Clamp((int)MathF.Round(v * 255f), 0, 255);
}
