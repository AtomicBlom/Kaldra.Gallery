using System.Numerics;
using Loom.Core.Geometry;
using Loom.Core.Graph;
using Loom.Core.Hosting;
using Loom.Core.Materials;

namespace Loom.Sandbox.Demos;

/// <summary>
/// 2D deferred normal-mapped lighting. The same batched ball sprites as <see cref="SpritesDemo"/> rasterise
/// into the 2D G-buffer (gbuffer_sprite: albedo + tangent-space normal + emissive), then real, data-driven
/// lights replace a fixed key light:
/// <list type="number">
/// <item>an <c>ambient2d</c> fullscreen base pass seeds the lit target with ambient·albedo + emissive;</item>
/// <item>a single <c>light2d</c> instanced <em>additive</em> draw places one quad per <see cref="Light2DInstance"/>
/// — each samples the G-buffer normal under its covered pixels and accumulates colour·intensity·N·L·attenuation,
/// so the count is unbounded and fully dynamic and overdraw is bounded by the lights' reach.</item>
/// </list>
/// The lights orbit (pulled from <c>ctx.Time</c>, not captured), so the spherical normal maps' relief shifts
/// under them — the unmistakable read of real 2D normal-mapped lighting. Output is HDR (Rgba16F) and a nearest
/// upscale blows the 320×180 world up to the screen. Camera is orthographic.
/// </summary>
internal sealed class Lighting2DDemo : IDemo
{
	private const int WorldWidth = 320;
	private const int WorldHeight = 180;

	private Material _atlas;
	private SpriteInstance[] _sprites = [];
	private IEngine _engine = null!;
	private Concepts.Camera _cam = null!;
	private ConstantsHandle _viewproj = null!;

	public string Name => "2D Lights";

	public void DefineTechniques(IEngine engine)
	{
		_engine = engine;
		_cam = new Concepts.Camera(engine);
		engine.DefineTechnique("sprite", new TechniqueDescriptor(
			"gbuffer_sprite", HasVertexInput: true, Layout: VertexLayout.MeshStandard, Instance: SandboxVertexLayouts.Sprite));
		engine.DefineTechnique("ambient2d", new TechniqueDescriptor("ambient2d"));
		// The 2D light volume: instanced (per-light stream) + additive (one .Blends accumulation onto the lit target).
		engine.DefineTechnique("light2d", new TechniqueDescriptor(
			"light2d", HasVertexInput: true, Blend: BlendMode.Additive, Layout: VertexLayout.MeshStandard, Instance: SandboxVertexLayouts.Light2D));
		engine.DefineTechnique("blit", new TechniqueDescriptor("blit"));

		_atlas = BallAtlas.CreateMaterial(engine, out var sheet);

		// The same 8×5 field of balls the sprite demo uses — but here the relief is driven by moving lights.
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

	// The same centred orthographic view as the sprite demo (the backend handles the Y-flip).
	public Concepts.Camera? GetCamera(float aspect, float time)
	{
		var eye = new Vector3(WorldWidth * 0.5f, WorldHeight * 0.5f, 10f);
		return _cam.Orthographic(WorldWidth, WorldHeight, 0.1f, 100f).LookAt(eye, eye with { Z = 0f });
	}

	public RenderGraph Build(DemoScene scene) => RenderGraph.Define(g =>
	{
		var screen = g.Backbuffer;
		var world = g.Domain("light2d", WorldWidth, WorldHeight);
		var albedo = world.Color("l2d_albedo", Format.Rgba8Srgb);
		var normal = world.Color("l2d_normal", Format.Rgba8Unorm);
		var emissive = world.Color("l2d_emissive", Format.Rgba8Srgb);
		var lit = world.Color("l2d_lit", Format.Rgba16F);
		_viewproj = g.Constants<Matrix4x4>("light2d.viewproj");

		// 1. One instanced draw rasterises every sprite into the 2D G-buffer (clears, then alpha-tested quads).
		g.Pass("sprites")
			.Reads(_viewproj)
			.Writes(albedo, normal, emissive)
			.Draw(ctx => ctx.Draw(new DrawCall { Mesh = scene.Quad, Material = _atlas, Technique = "sprite", Instances = DrawCall.From(_sprites.AsSpan()) }));

		// 2. Base term: ambient·albedo + emissive seeds the (HDR) lit target before the lights accumulate.
		g.Pass("ambient")
			.Samples(albedo, emissive)
			.Writes(lit)
			.Draw(ctx => ctx.FullScreen("ambient2d", albedo, emissive));

		// 3. The lights: one instanced additive draw, each quad a light sampling the G-buffer normal it covers.
		//    Light data is built per-frame from ctx.Time (pulled, not captured) so the lights orbit the field.
		g.Pass("lights")
			.Reads(_viewproj)
			.Samples(albedo, normal)
			.Blends(lit)
			.Draw(ctx =>
			{
				var t = ctx.Time;
				var centre = new Vector2(WorldWidth * 0.5f, WorldHeight * 0.5f);
				Span<Light2DInstance> lights =
				[
					Light2DInstance.At(centre + Orbit(t * 0.60f, 82f), 122f, new Vector3(1.00f, 0.45f, 0.30f), 1.7f, 34f),
					Light2DInstance.At(centre + Orbit(t * -0.45f + 2.1f, 96f), 132f, new Vector3(0.30f, 0.80f, 0.95f), 1.5f, 30f),
					Light2DInstance.At(centre + Orbit(t * 0.90f + 4.2f, 60f), 108f, new Vector3(0.45f, 0.95f, 0.40f), 1.4f, 44f),
				];
				ReadOnlySpan<ResourceHandle> gbuffer = [albedo, normal];
				ctx.Draw(new DrawCall { Mesh = scene.Quad, Technique = "light2d", Instances = DrawCall.From<Light2DInstance>(lights), Sampled = gbuffer });
			});

		// 4. Nearest upscale to the backbuffer.
		g.Pass("upscale")
			.Samples(lit)
			.Writes(screen)
			.Draw(ctx => ctx.FullScreen("blit", lit));

		DemoGraph.Hud(g, screen, scene);
		g.Present(screen);
	});

	// Publish this frame's orthographic view-projection for the sprite + light passes to read.
	public void Update(Concepts.Camera camera) => _engine.Write(_viewproj, camera.ViewProjection);

	public string HudStatus => $"3 lights · {_sprites.Length} sprites (1 instanced additive draw)";

	// A point on a circle of the given radius (cos, sin) — the per-light orbit offset.
	private static Vector2 Orbit(float angle, float radius) => new(MathF.Cos(angle) * radius, MathF.Sin(angle) * radius);
}
