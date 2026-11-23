using System.Numerics;
using BinaryVibrance.Kaldra.Core.Hosting;
using BinaryVibrance.Kaldra.Core.Materials;

namespace Kaldra.Gallery.Demos;

/// <summary>
/// A procedurally-built 4×4 tile atlas for <see cref="TilemapDemo"/>: 16 tiles of 16×16 texels, tile 0
/// left transparent (the "sky"/empty cell the tilemap shader discards) and the rest distinct solid materials with
/// a beveled tangent-space normal map, so each cell reads as a slightly raised block under the 2D deferred light.
/// The atlas grid (4×4) is the contract <c>tilemap.slang</c>'s <c>AtlasTiles</c> constant bakes. Deterministic
/// bytes → safe for a cross-backend golden.
/// </summary>
internal static class TileAtlas
{
	public const int TilesAcross = 4;
	public const int TilesDown = 4;
	private const int TilePx = 16;

	// Tile id → base colour (linear-ish). Index 0 is the empty/transparent sky cell.
	private static readonly Vector3[] Colors =
	[
		default,                    // 0  sky (transparent)
		new(0.32f, 0.62f, 0.30f),   // 1  grass
		new(0.46f, 0.33f, 0.22f),   // 2  dirt
		new(0.50f, 0.50f, 0.55f),   // 3  stone
		new(0.66f, 0.30f, 0.26f),   // 4  brick
		new(0.82f, 0.74f, 0.45f),   // 5  sand
		new(0.28f, 0.46f, 0.72f),   // 6  blue crystal
		new(0.55f, 0.42f, 0.26f),   // 7  wood
		new(0.24f, 0.48f, 0.34f),   // 8  dark grass
		new(0.58f, 0.45f, 0.32f),   // 9  light dirt
		new(0.38f, 0.40f, 0.46f),   // 10 dark stone
		new(0.40f, 0.60f, 0.42f),   // 11 moss
		new(0.70f, 0.82f, 0.88f),   // 12 ice
		new(0.85f, 0.45f, 0.20f),   // 13 lava rock
		new(0.52f, 0.36f, 0.62f),   // 14 amethyst
		new(0.80f, 0.80f, 0.82f),   // 15 pale stone
	];

	public static void CreateTextures(IEngine engine, out Texture albedo, out Texture normal)
	{
		var (a, n) = BuildPixels(out var width, out var height);
		albedo = engine.CreateTexture(TextureData.FromRgba8(width, height, a, TextureFormat.Rgba8Srgb, generateMips: false));
		normal = engine.CreateTexture(TextureData.FromRgba8(width, height, n, TextureFormat.Rgba8Unorm, generateMips: false));
	}

	private static (byte[] Albedo, byte[] Normal) BuildPixels(out int width, out int height)
	{
		width = TilesAcross * TilePx;  // 64
		height = TilesDown * TilePx;   // 64
		var albedo = new byte[width * height * 4];
		var normal = new byte[width * height * 4];

		const float bevel = 0.30f;     // fraction of the tile that slopes at each edge
		const float strength = 0.65f;  // how hard the edge normal tilts (the "raised block" read)

		for (var ty = 0; ty < TilesDown; ty++)
			for (var tx = 0; tx < TilesAcross; tx++)
			{
				var id = ty * TilesAcross + tx;
				var empty = id == 0;
				var color = Colors[id];
				for (var py = 0; py < TilePx; py++)
					for (var px = 0; px < TilePx; px++)
					{
						var gx = tx * TilePx + px;
						var gy = ty * TilePx + py;
						var o = (gy * width + gx) * 4;

						if (empty)
						{
							// Transparent (alpha 0 → the tilemap shader discards it); flat normal just in case.
							normal[o + 0] = 128;
							normal[o + 1] = 128;
							normal[o + 2] = 255;
							normal[o + 3] = 255;
							continue;
						}

						var ex = (px + 0.5f) / TilePx;
						var ey = (py + 0.5f) / TilePx;
						var nx = EdgeTilt(ex, bevel) * strength;
						var ny = -EdgeTilt(ey, bevel) * strength; // -: image-top → +Y world
						var nz = MathF.Sqrt(MathF.Max(1e-4f, 1f - nx * nx - ny * ny));
						var n = Vector3.Normalize(new Vector3(nx, ny, nz));

						// A touch of edge darkening for a grout/groove read between blocks.
						var shade = 1f - 0.18f * EdgeDarken(ex, ey, bevel);
						albedo[o + 0] = ToByte(color.X * shade);
						albedo[o + 1] = ToByte(color.Y * shade);
						albedo[o + 2] = ToByte(color.Z * shade);
						albedo[o + 3] = 255;
						normal[o + 0] = ToByte(n.X * 0.5f + 0.5f);
						normal[o + 1] = ToByte(n.Y * 0.5f + 0.5f);
						normal[o + 2] = ToByte(n.Z * 0.5f + 0.5f);
						normal[o + 3] = 255;
					}
			}

		return (albedo, normal);
	}

	// -1 at the left edge band, +1 at the right edge band, 0 in the flat middle — a symmetric bevel.
	private static float EdgeTilt(float t, float bevel) =>
		t < bevel ? -(1f - t / bevel)
		: t > 1f - bevel ? 1f - (1f - t) / bevel
		: 0f;

	// 1 at the very edge, ramping to 0 by `bevel` in from the nearest edge.
	private static float EdgeDarken(float ex, float ey, float bevel)
	{
		var d = MathF.Min(MathF.Min(ex, 1f - ex), MathF.Min(ey, 1f - ey));
		return d < bevel ? 1f - d / bevel : 0f;
	}

	private static byte ToByte(float v) => (byte)Math.Clamp((int)MathF.Round(v * 255f), 0, 255);
}
