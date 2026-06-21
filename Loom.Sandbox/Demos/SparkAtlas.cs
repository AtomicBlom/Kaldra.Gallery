using Loom.Assets;
using Loom.Core.Hosting;
using Loom.Core.Materials;

namespace Loom.Sandbox.Demos;

/// <summary>
/// The 4-frame spark atlas for the generalized GPU particle system (M14-4) — a 4×1 grid of soft white radial
/// glows that evolve left-to-right from a young, tight, bright core to an old, wide, faint puff. The render
/// shader (<c>particle_glow_render.slang</c>) picks the frame from a particle's normalised life (sub-UV) and
/// tints it with a warm ember colour-over-life ramp, additively blended for bloom. White luminance lives in
/// rgb (the shader reads <c>.r</c>); the byte output is deterministic → safe for cross-backend golden images.
/// </summary>
internal static class SparkAtlas
{
	private const int Cell = 32, Frames = 4;

	/// <summary>Build the spark atlas as a single linear (Unorm) texture material, plus its 4×1 sprite sheet.</summary>
	public static Material CreateMaterial(IEngine engine, out SpriteSheet sheet)
	{
		var pixels = BuildPixels(out var width, out var height);
		sheet = new SpriteSheet(Frames, 1, width, height, insetTexels: 0.5f);
		return engine.CreateMaterial(
			engine.CreateTexture(TextureData.FromRgba8(width, height, pixels, TextureFormat.Rgba8Unorm, generateMips: false)));
	}

	// Each frame is a radial falloff value^exponent · peak; later frames widen, soften, and dim — a spark
	// cooling into a diffuse puff. Linear (not sRGB) so the falloff stays literal; sampled with linear filtering.
	private static byte[] BuildPixels(out int width, out int height)
	{
		width = Frames * Cell;
		height = Cell;
		var pixels = new byte[width * height * 4];

		// (radius as a fraction of the half-cell, falloff exponent, peak brightness) per frame.
		(float radius, float exponent, float peak)[] frames =
		[
			(0.50f, 2.5f, 1.00f), // young: tight bright core
			(0.62f, 2.0f, 0.82f),
			(0.78f, 1.6f, 0.62f),
			(0.92f, 1.3f, 0.42f), // old: wide faint puff
		];

		const float centre = Cell * 0.5f;
		for (var f = 0; f < Frames; f++)
		{
			var (radiusFrac, exponent, peak) = frames[f];
			var radius = centre * radiusFrac;
			for (var py = 0; py < Cell; py++)
				for (var px = 0; px < Cell; px++)
				{
					var dx = px + 0.5f - centre;
					var dy = py + 0.5f - centre;
					var nd = MathF.Sqrt(dx * dx + dy * dy) / radius;
					var value = nd >= 1f ? 0f : MathF.Pow(1f - nd, exponent) * peak;

					var gx = f * Cell + px;
					var o = (py * width + gx) * 4;
					var b = ToByte(value);
					pixels[o + 0] = b;
					pixels[o + 1] = b;
					pixels[o + 2] = b;
					pixels[o + 3] = b;
				}
		}

		return pixels;
	}

	private static byte ToByte(float v) => (byte)Math.Clamp((int)MathF.Round(v * 255f), 0, 255);
}
