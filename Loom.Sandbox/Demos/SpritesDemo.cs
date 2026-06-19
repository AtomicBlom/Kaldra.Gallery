using System.Numerics;

using Loom.Assets;
using Loom.Core;

namespace Loom.Sandbox.Demos;

/// <summary>
/// Sprite batching + texture atlases (PlanningPhase2.md M14-1, Track A). One <c>ctx.DrawInstanced</c> places a
/// whole field of quads — each a <see cref="SpriteInstance"/> in the per-instance stream (<see
/// cref="VertexLayout.Sprite"/>) sharing one atlas material — into a low-res 2D deferred G-buffer
/// (gbuffer_sprite.slang: albedo + tangent-space normal + emissive), which a compose pass lights and a nearest
/// upscale blows up to the screen (the §9 pixelation). The camera is orthographic (<see cref="GetCamera"/>).
/// <para>
/// The atlas is a procedurally-built 2×2 sheet of coloured balls with spherical normal maps, so the sprites read
/// as lit 3D under the compose's fixed key light — the relief the real 2D light volumes (M14-3) will drive. The
/// per-instance uv-rect (from a <see cref="SpriteSheet"/>) selects each ball's cell; animating it over time is
/// sprite-sheet animation (M14-5), which extends this demo.
/// </para>
/// </summary>
internal sealed class SpritesDemo : IDemo
{
	private const int WorldWidth = 320;
	private const int WorldHeight = 180;

	private Material _atlas;
	private SpriteInstance[] _sprites = [];

	public string Name => "Sprites + Atlas";

	public void DefineTechniques(IEngine engine)
	{
		engine.DefineTechnique("sprite", new TechniqueDescriptor(
			"gbuffer_sprite", HasVertexInput: true, Layout: VertexLayout.MeshStandard, Instance: VertexLayout.Sprite));
		engine.DefineTechnique("compose2d", new TechniqueDescriptor("compose2d"));
		engine.DefineTechnique("blit", new TechniqueDescriptor("blit"));

		// Build the atlas once (the engine is in hand here): a 2×2 sheet of coloured balls + their spherical
		// normal maps, uploaded as an albedo + normal-map material. No mips — pixel art samples nearest.
		var (albedoPixels, normalPixels) = BuildBallAtlas(out var atlasW, out var atlasH);
		_atlas = engine.CreateMaterial(
			engine.CreateTexture(TextureData.FromRgba8(atlasW, atlasH, albedoPixels, TextureFormat.Rgba8Srgb, generateMips: false)),
			engine.CreateTexture(TextureData.FromRgba8(atlasW, atlasH, normalPixels, TextureFormat.Rgba8Unorm, generateMips: false)));

		// Lay out a field of sprites — one DrawInstanced batches them all. Cells cycle so the atlas is visible.
		var sheet = new SpriteSheet(2, 2, atlasW, atlasH, insetTexels: 0.5f);
		const int cols = 8, rows = 5;
		var sprites = new SpriteInstance[cols * rows];
		for (var r = 0; r < rows; r++)
			for (var c = 0; c < cols; c++)
			{
				var i = r * cols + c;
				var pos = new Vector2((c + 0.5f) * (WorldWidth / (float)cols), (r + 0.5f) * (WorldHeight / (float)rows));
				sprites[i] = SpriteInstance.At(pos, new Vector2(28f, 28f), sheet.Uv(i % sheet.Count));
			}
		_sprites = sprites;
	}

	// An orthographic view of the [0,WorldWidth]×[0,WorldHeight] world, centred on the camera (so the projection
	// stays symmetric and the backend's clip-space Y-flip behaves), +Y up. Time-independent for now; M14-6 scrolls it.
	public Camera? GetCamera(float aspect, float time)
	{
		var eye = new Vector3(WorldWidth * 0.5f, WorldHeight * 0.5f, 10f);
		var view = Matrix4x4.CreateLookAt(eye, eye with { Z = 0f }, Vector3.UnitY);
		var projection = Matrix4x4.CreateOrthographic(WorldWidth, WorldHeight, 0.1f, 100f);
		return new Camera(view, projection);
	}

	public RenderGraph Build(DemoScene scene) => RenderGraph.Define(g =>
	{
		var screen = g.Backbuffer;
		var world = g.Domain("sprite2d", WorldWidth, WorldHeight);
		var albedo = world.Color("sprite_albedo", Format.Rgba8Srgb);
		var normal = world.Color("sprite_normal", Format.Rgba8Unorm);
		var emissive = world.Color("sprite_emissive", Format.Rgba8Srgb);
		var lit = world.Color("sprite_lit", Format.Rgba16F);

		// One instanced draw rasterises every sprite into the 2D G-buffer (clears, then alpha-tested quads).
		g.Pass("sprites")
			.Writes(albedo, normal, emissive)
			.Draw(ctx => ctx.DrawInstanced(scene.Quad, _atlas, "sprite", _sprites.AsSpan()));

		// Compose the G-buffer into a lit (HDR) target — a preview ahead of the real 2D light volumes (M14-3).
		g.Pass("compose")
			.Samples(albedo, normal, emissive)
			.Writes(lit)
			.Draw(ctx => ctx.FullScreen("compose2d", albedo, normal, emissive));

		// Nearest upscale to the backbuffer — the §9 pixelation.
		g.Pass("upscale")
			.Samples(lit)
			.Writes(screen)
			.Draw(ctx => ctx.FullScreen("blit", lit));

		DemoGraph.Hud(g, screen);
		g.Present(screen);
	});

	public string? HudStatus => $"sprites {_sprites.Length} (1 batched draw)";

	// A 2×2 atlas of coloured balls: each cell a filled circle (alpha 1 inside, 0 outside for alpha-test) with a
	// spherical tangent-space normal map, so the sprites light as little 3D balls. Row-major cell order matches
	// SpriteSheet.Uv. Deterministic → safe for cross-backend golden images.
	private static (byte[] Albedo, byte[] Normal) BuildBallAtlas(out int width, out int height)
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
