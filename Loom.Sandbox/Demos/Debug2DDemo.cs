using System.Numerics;
using Loom.Core.Geometry;
using Loom.Core.Graph;
using Loom.Core.Hosting;
using Loom.Core.Materials;

namespace Loom.Sandbox.Demos;

/// <summary>
/// 2D debug overlays — the debug view for the deferred 2D pipeline. Takes the lit ball scene and makes the
/// pipeline legible:
/// <list type="bullet">
/// <item><b>Light gizmos</b> — one additive ring per light (gizmo2d), drawn over the lit target from the <em>same</em>
/// <see cref="Light2DInstance"/> stream the real lights use, so each light's position + reach is shown as a wireframe
/// that can't drift from what's actually shading the scene.</item>
/// <item><b>G-buffer channel inspector</b> — the terminal pass (gbufview2d) tiles albedo, the encoded normal (as RGB),
/// the normal's facing (grayscale n.z), and the final lit result into four quadrants — the in-graph counterpart to
/// the resource viewer, focused on the 2D deferred targets.</item>
/// </list>
/// Lights orbit from <c>ctx.Time</c> (pulled, not captured). Camera is orthographic. No host or backend changes —
/// pure graph + two small shaders on the existing 2D deferred path.
/// </summary>
internal sealed class Debug2DDemo : IDemo
{
	private const int WorldWidth = 320;
	private const int WorldHeight = 180;

	private Material _atlas;
	private SpriteInstance[] _sprites = [];

	public string Name => "2D Debug";

	public void DefineTechniques(IEngine engine)
	{
		engine.DefineTechnique("sprite", new TechniqueDescriptor(
			"gbuffer_sprite", HasVertexInput: true, Layout: VertexLayout.MeshStandard, Instance: SandboxVertexLayouts.Sprite));
		engine.DefineTechnique("ambient2d", new TechniqueDescriptor("ambient2d"));
		engine.DefineTechnique("light2d", new TechniqueDescriptor(
			"light2d", HasVertexInput: true, Blend: BlendMode.Additive, Layout: VertexLayout.MeshStandard, Instance: SandboxVertexLayouts.Light2D));
		engine.DefineTechnique("gizmo2d", new TechniqueDescriptor(
			"gizmo2d", HasVertexInput: true, Blend: BlendMode.Additive, Layout: VertexLayout.MeshStandard, Instance: SandboxVertexLayouts.Light2D));
		engine.DefineTechnique("gbufview2d", new TechniqueDescriptor("gbufview2d"));

		_atlas = BallAtlas.CreateMaterial(engine, out var sheet);

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
		var world = g.Domain("debug2d", WorldWidth, WorldHeight);
		var albedo = world.Color("dbg_albedo", Format.Rgba8Srgb);
		var normal = world.Color("dbg_normal", Format.Rgba8Unorm);
		var emissive = world.Color("dbg_emissive", Format.Rgba8Srgb);
		var lit = world.Color("dbg_lit", Format.Rgba16F);

		g.Pass("sprites")
			.Writes(albedo, normal, emissive)
			.Draw(ctx => ctx.Draw(new DrawCall { Mesh = scene.Quad, Material = _atlas, Technique = "sprite", Instances = DrawCall.From(_sprites.AsSpan()) }));

		g.Pass("ambient")
			.Samples(albedo, emissive)
			.Writes(lit)
			.Draw(ctx => ctx.FullScreen("ambient2d", albedo, emissive));

		g.Pass("lights")
			.Samples(albedo, normal)
			.Blends(lit)
			.Draw(ctx =>
			{
				var lights = BuildLights(ctx.Time);
				ReadOnlySpan<ResourceHandle> gbuffer = [albedo, normal];
				ctx.Draw(new DrawCall { Mesh = scene.Quad, Technique = "light2d", Instances = DrawCall.From(lights.AsSpan()), Sampled = gbuffer });
			});

		// Gizmos: the same lights drawn as additive rings over the lit result (no textures, no G-buffer sample).
		g.Pass("gizmos")
			.Blends(lit)
			.Draw(ctx => ctx.Draw(new DrawCall { Mesh = scene.Quad, Technique = "gizmo2d", Instances = DrawCall.From(BuildLights(ctx.Time).AsSpan()) }));

		// Terminal pass: tile the G-buffer channels + lit into four quadrants, upscaled to the screen.
		g.Pass("inspect")
			.Samples(albedo, normal, lit)
			.Writes(screen)
			.Draw(ctx => ctx.FullScreen("gbufview2d", albedo, normal, lit));

		DemoGraph.Hud(g, screen);
		g.Present(screen);
	});

	public string HudStatus => "G-buffer quadrants + light gizmos";

	// The same three orbiting lights as Lighting2DDemo — built per frame from ctx.Time.
	private static Light2DInstance[] BuildLights(float t)
	{
		var centre = new Vector2(WorldWidth * 0.5f, WorldHeight * 0.5f);
		return
		[
			Light2DInstance.At(centre + Orbit(t * 0.60f, 82f), 122f, new Vector3(1.00f, 0.45f, 0.30f), 1.7f, 34f),
			Light2DInstance.At(centre + Orbit(t * -0.45f + 2.1f, 96f), 132f, new Vector3(0.30f, 0.80f, 0.95f), 1.5f, 30f),
			Light2DInstance.At(centre + Orbit(t * 0.90f + 4.2f, 60f), 108f, new Vector3(0.45f, 0.95f, 0.40f), 1.4f, 44f),
		];
	}

	private static Vector2 Orbit(float angle, float radius) => new(MathF.Cos(angle) * radius, MathF.Sin(angle) * radius);
}
