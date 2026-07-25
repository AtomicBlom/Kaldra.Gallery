using System.Numerics;

using Loom.Assets;
using Loom.Core.Hosting;
using Loom.Core.Materials;

namespace Loom.Gallery.Demos;

/// <summary>
/// The shared 2×2 atlas of coloured balls used by the Track-A 2D demos (<see cref="SpritesDemo"/>,
/// <see cref="Lighting2DDemo"/>). Each cell is a filled circle (alpha 1 inside, 0 outside for alpha-test)
/// with a spherical tangent-space normal map, so the sprites read as lit 3D balls under a 2D light — the relief
/// the light volumes drive. Row-major cell order matches <see cref="SpriteSheet.Uv"/>. The byte output is
/// deterministic → safe for cross-backend golden images.
/// </summary>
internal static class BallAtlas
{
	/// <summary>Build the atlas as an albedo (sRGB) + tangent-space normal (linear) material, plus its sprite sheet.</summary>
	public static Material CreateMaterial(IEngine engine, out SpriteSheet sheet)
	{
		var (albedo, normal) = BuildPixels(out var width, out var height);
		sheet = new SpriteSheet(2, 2, width, height, insetTexels: 0.5f);
		return engine.CreateMaterial(
			engine.CreateTexture(TextureData.FromRgba8(width, height, albedo, TextureFormat.Rgba8Srgb, generateMips: false)),
			engine.CreateTexture(TextureData.FromRgba8(width, height, normal, TextureFormat.Rgba8Unorm, generateMips: false)));
	}

	// A 2×2 atlas of coloured balls: each cell a filled circle with a spherical normal map. No mips — pixel art
	// samples nearest.
	private static (byte[] Albedo, byte[] Normal) BuildPixels(out int width, out int height)
	{
		const int cell = 32, cols = 2, rows = 2;
		width = cols * cell;
		height = rows * cell;
		var albedo = new byte[width * height * 4];
		var normal = new byte[width * height * 4];

		Vector3[] colors =
		[
			new(0.95f, 0.30f, 0.30f), // red
			new(0.35f, 0.85f, 0.45f), // green
			new(0.40f, 0.55f, 0.95f), // blue
			new(0.95f, 0.80f, 0.30f), // yellow
		];

		const float radius = cell * 0.45f;
		const float centre = cell * 0.5f;
		for (var cy = 0; cy < rows; cy++)
			for (var cx = 0; cx < cols; cx++)
			{
				var color = colors[cy * cols + cx];
				for (var py = 0; py < cell; py++)
					for (var px = 0; px < cell; px++)
					{
						var dx = px + 0.5f - centre;
						var dy = py + 0.5f - centre;
						var nd = MathF.Sqrt(dx * dx + dy * dy) / radius;
						var gx = cx * cell + px;
						var gy = cy * cell + py;
						var o = (gy * width + gx) * 4;

						if (nd <= 1f)
						{
							var nz = MathF.Sqrt(MathF.Max(0f, 1f - nd * nd));
							var n = Vector3.Normalize(new Vector3(dx / radius, -dy / radius, nz)); // -dy: image-top → +Y
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
							// Outside the ball: transparent (alpha-tested away); flat normal just in case.
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
