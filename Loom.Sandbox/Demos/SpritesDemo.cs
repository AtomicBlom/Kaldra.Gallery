using System.Numerics;

using Loom.Assets;
using Loom.Core.Geometry;
using Loom.Core.Graph;
using Loom.Core.Hosting;
using Loom.Core.Materials;

namespace Loom.Sandbox.Demos;

/// <summary>
/// Sprite batching + texture atlases (PlanningPhase2.md M14-1, Track A). One <c>ctx.DrawInstanced</c> places a
/// whole field of quads — each a <see cref="SpriteInstance"/> in the per-instance stream (<see
/// cref="SandboxVertexLayouts.Sprite"/>) sharing one atlas material — into a low-res 2D deferred G-buffer
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
			"gbuffer_sprite", HasVertexInput: true, Layout: VertexLayout.MeshStandard, Instance: SandboxVertexLayouts.Sprite));
		engine.DefineTechnique("compose2d", new TechniqueDescriptor("compose2d"));
		engine.DefineTechnique("blit", new TechniqueDescriptor("blit"));

		// Build the shared 2×2 ball atlas (albedo + spherical normal map) — see BallAtlas.
		_atlas = BallAtlas.CreateMaterial(engine, out var sheet);

		// Lay out a field of sprites — one DrawInstanced batches them all. Cells cycle so the atlas is visible.
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
			.Draw(ctx => ctx.Draw(new DrawCall { Mesh = scene.Quad, Material = _atlas, Technique = "sprite", Instances = DrawCall.From(_sprites.AsSpan()) }));

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

	public string HudStatus => $"sprites {_sprites.Length} (1 batched draw)";
}
