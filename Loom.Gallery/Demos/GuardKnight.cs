using System.Numerics;

using Loom.Assets;
using Loom.Core.Hosting;
using Loom.Core.Materials;

namespace Loom.Gallery.Demos;

/// <summary>
/// A procedurally-built 4-frame walk strip for <see cref="PixelArtCastleDemo"/>'s patrolling guards: a small
/// armoured knight — domed helmet with a dark visor slit, a steel breastplate, an upright spear, and two legs that
/// stride per frame — so cycling the frames reads as a guard marching (replacing the abstract spinning disc). A
/// spherical-ish tangent-space normal rides along so the guard still catches the floor torches' 2D relief. Frame
/// order matches <see cref="SpriteSheet.Uv"/>; deterministic bytes → safe for a cross-backend golden.
/// </summary>
internal static class GuardKnight
{
	public const int Frames = 4;
	private const int W = 24;
	private const int H = 32;

	public static Material CreateMaterial(IEngine engine, out SpriteSheet sheet)
	{
		var (albedo, normal) = BuildPixels();
		sheet = new SpriteSheet(Frames, 1, Frames * W, H, insetTexels: 0.5f);
		return engine.CreateMaterial(
			engine.CreateTexture(TextureData.FromRgba8(Frames * W, H, albedo, TextureFormat.Rgba8Srgb, generateMips: false)),
			engine.CreateTexture(TextureData.FromRgba8(Frames * W, H, normal, TextureFormat.Rgba8Unorm, generateMips: false)));
	}

	private static (byte[] Albedo, byte[] Normal) BuildPixels()
	{
		var width = Frames * W;
		var albedo = new byte[width * H * 4];
		var normal = new byte[width * H * 4];
		for (var i = 0; i < normal.Length; i += 4) { normal[i] = 128; normal[i + 1] = 128; normal[i + 2] = 255; normal[i + 3] = 255; }

		var armor = new Vector3(0.42f, 0.47f, 0.58f);
		var helmet = new Vector3(0.56f, 0.61f, 0.72f);
		var visor = new Vector3(0.08f, 0.09f, 0.13f);
		var plume = new Vector3(0.80f, 0.25f, 0.25f);
		var shaft = new Vector3(0.46f, 0.32f, 0.20f);
		var tip = new Vector3(0.78f, 0.80f, 0.88f);

		for (var f = 0; f < Frames; f++)
		{
			var ox = f * W;
			var cx = W / 2;
			// Stride: legs swing out on frames 1/3, together on 0/2 (a simple two-beat march).
			var stride = f == 1 ? 2 : f == 3 ? -2 : 0;

			// Plume on top of the helmet.
			for (var py = 1; py <= 3; py++)
				Put(albedo, ox + cx, py, plume, width);

			// Helmet dome (rows 4–11).
			for (var py = 4; py <= 11; py++)
				for (var dx = -4; dx <= 4; dx++)
				{
					var rim = MathF.Abs(dx) + MathF.Abs(py - 8) * 0.6f;
					if (rim <= 5f)
						Put(albedo, ox + cx + dx, py, helmet, width);
				}
			// Visor slit.
			for (var dx = -3; dx <= 3; dx++)
				Put(albedo, ox + cx + dx, 8, visor, width);

			// Breastplate / body (rows 12–22), a slight taper.
			for (var py = 12; py <= 22; py++)
			{
				var half = py < 14 ? 5 : 4; // shoulders wider
				for (var dx = -half; dx <= half; dx++)
					Put(albedo, ox + cx + dx, py, armor, width);
			}

			// Spear: an upright shaft on the right with a steel tip.
			for (var py = 1; py <= 28; py++)
				Put(albedo, ox + cx + 7, py, shaft, width);
			for (var py = 0; py <= 3; py++)
				Put(albedo, ox + cx + 7, py, tip, width);

			// Legs (rows 23–31), striding.
			for (var py = 23; py <= 31; py++)
			{
				Put(albedo, ox + cx - 3 + (stride > 0 ? -stride : 0), py, armor, width); // back leg
				Put(albedo, ox + cx - 2 + (stride > 0 ? -stride : 0), py, armor, width);
				Put(albedo, ox + cx + 2 + (stride < 0 ? -stride : 0), py, armor, width); // front leg
				Put(albedo, ox + cx + 3 + (stride < 0 ? -stride : 0), py, armor, width);
			}
		}

		return (albedo, normal);
	}

	private static void Put(byte[] rgba, int x, int y, Vector3 color, int width)
	{
		if (x < 0 || x >= width || y < 0 || y >= H) return;
		var o = (y * width + x) * 4;
		rgba[o + 0] = ToByte(color.X);
		rgba[o + 1] = ToByte(color.Y);
		rgba[o + 2] = ToByte(color.Z);
		rgba[o + 3] = 255;
	}

	private static byte ToByte(float v) => (byte)Math.Clamp((int)MathF.Round(v * 255f), 0, 255);
}
