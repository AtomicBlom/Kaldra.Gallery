using System.Numerics;

using Loom.Assets;
using Loom.Core.Hosting;
using Loom.Core.Materials;

namespace Loom.Sandbox.Demos;

/// <summary>
/// A procedurally-built 2×2 atlas of distant castle silhouettes for <see cref="PixelArtCastleDemo"/>'s parallax
/// backdrop: a broad battlemented tower, a tall turret with a pointed roof, a glowing moon, and a low wall segment.
/// Each is a dark night-blue silhouette (with a couple of warm lit windows) on a transparent field, so a row of
/// them reads as a castle skyline behind the foreground rampart. A flat tangent-space normal rides along (the 2D
/// G-buffer expects a normal slot); the bytes are deterministic → safe for a cross-backend golden.
/// </summary>
internal static class CastleBackdrop
{
	public const int Tower = 0;   // broad battlemented tower
	public const int Turret = 1;  // tall turret with a pointed roof
	public const int Moon = 2;    // glowing moon disc
	public const int Wall = 3;    // low battlemented wall segment

	private const int Cell = 48;

	public static Material CreateMaterial(IEngine engine, out SpriteSheet sheet)
	{
		var (albedo, normal) = BuildPixels();
		sheet = new SpriteSheet(2, 2, 2 * Cell, 2 * Cell, insetTexels: 0.5f);
		return engine.CreateMaterial(
			engine.CreateTexture(TextureData.FromRgba8(2 * Cell, 2 * Cell, albedo, TextureFormat.Rgba8Srgb, generateMips: false)),
			engine.CreateTexture(TextureData.FromRgba8(2 * Cell, 2 * Cell, normal, TextureFormat.Rgba8Unorm, generateMips: false)));
	}

	private static (byte[] Albedo, byte[] Normal) BuildPixels()
	{
		const int w = 2 * Cell, h = 2 * Cell;
		var albedo = new byte[w * h * 4];
		var normal = new byte[w * h * 4];
		for (var i = 0; i < normal.Length; i += 4) { normal[i] = 128; normal[i + 1] = 128; normal[i + 2] = 255; normal[i + 3] = 255; }

		DrawTower(albedo, Tower, bodyHalf: 14, baseY: 46, topY: 10, merlons: true, roof: false);
		DrawTower(albedo, Turret, bodyHalf: 9, baseY: 46, topY: 4, merlons: false, roof: true);
		DrawMoon(albedo, Moon);
		DrawTower(albedo, Wall, bodyHalf: 22, baseY: 46, topY: 26, merlons: true, roof: false);

		return (albedo, normal);
	}

	// A tower silhouette centred in its cell: a dark night-blue body from baseY up to topY, optional crenellated top
	// (merlons) or a pointed roof, plus a couple of warm lit windows.
	private static void DrawTower(byte[] rgba, int cell, int bodyHalf, int baseY, int topY, bool merlons, bool roof)
	{
		var cx = cell % 2 * Cell;
		var cy = cell / 2 * Cell;
		var body = new Vector3(0.16f, 0.18f, 0.30f);   // dark night-blue stone
		var window = new Vector3(0.95f, 0.72f, 0.35f);  // warm lit window
		var centre = Cell / 2;

		for (var py = topY; py <= baseY; py++)
		{
			// Crenellations: along the top band, punch gaps every few columns to make merlons.
			var inTopBand = py < topY + 4;
			for (var dx = -bodyHalf; dx <= bodyHalf; dx++)
			{
				if (merlons && inTopBand && ((dx + bodyHalf) / 3) % 2 == 1)
					continue; // a gap between merlons
				Put(rgba, cx + centre + dx, cy + py, body);
			}
		}

		if (roof)
		{
			// A pointed roof: a triangle above the body.
			for (var py = 0; py < topY; py++)
			{
				var half = (int)(bodyHalf * (py / (float)Math.Max(topY, 1)));
				for (var dx = -half; dx <= half; dx++)
					Put(rgba, cx + centre + dx, cy + py, new Vector3(0.30f, 0.16f, 0.18f)); // dark red roof
			}
		}

		// Two warm windows down the body.
		foreach (var wy in new[] { topY + 12, topY + 24 })
		{
			if (wy + 2 > baseY) continue;
			for (var ox = -1; ox <= 1; ox++)
				for (var oy = 0; oy <= 2; oy++)
					Put(rgba, cx + centre + ox, cy + wy + oy, window);
		}
	}

	private static void DrawMoon(byte[] rgba, int cell)
	{
		var cx = cell % 2 * Cell;
		var cy = cell / 2 * Cell;
		const float centre = Cell / 2f;
		const float radius = Cell * 0.34f;
		for (var py = 0; py < Cell; py++)
			for (var px = 0; px < Cell; px++)
			{
				var d = MathF.Sqrt((px + 0.5f - centre) * (px + 0.5f - centre) + (py + 0.5f - centre) * (py + 0.5f - centre));
				if (d > radius) continue;
				var glow = 1f - 0.35f * (d / radius);
				Put(rgba, cx + px, cy + py, new Vector3(0.86f * glow, 0.88f * glow, 0.96f * glow));
			}
	}

	private static void Put(byte[] rgba, int x, int y, Vector3 color)
	{
		var o = (y * (2 * Cell) + x) * 4;
		rgba[o + 0] = ToByte(color.X);
		rgba[o + 1] = ToByte(color.Y);
		rgba[o + 2] = ToByte(color.Z);
		rgba[o + 3] = 255;
	}

	private static byte ToByte(float v) => (byte)Math.Clamp((int)MathF.Round(v * 255f), 0, 255);
}
