using System.Numerics;
using Loom.Core.Geometry;
using Loom.Core.Graph;
using Loom.Core.Hosting;
using Loom.Core.Materials;

namespace Loom.Sandbox.Demos;

/// <summary>
/// Parallax layers + 2D draw ordering. Several depth layers of sprites scroll
/// at speeds scaled by their depth (far layers crawl, the foreground races) — the parallax read of 2D depth — and
/// a depth target gives the 2D G-buffer real occlusion: nearer layers write smaller depth and occlude farther
/// ones (the ordering convention), so a foreground sprite passing a background one draws over it. All layers ride
/// in ONE instanced sprite draw (they share the atlas); the per-instance <see cref="SpriteInstance.Depth"/> places
/// each in its layer, and the depth test resolves who wins per pixel.
/// <para>
/// The scroll offset is pulled from <c>ctx.Time</c> (not captured), so the world scrolls while the camera stays
/// fixed — content moves, not the ortho view. With
/// the eye at z=10 looking down −Z, a <em>larger</em> world z is nearer, so the foreground layer takes the largest
/// <see cref="SpriteInstance.Depth"/> and the LessOrEqual depth test lets it occlude the rest.
/// </para>
/// </summary>
internal sealed class ParallaxDemo : IDemo
{
	private const int WorldWidth = 320;
	private const int WorldHeight = 180;
	private const float ScrollSpeed = 28f; // world units / second

	// One parallax layer: which atlas cell, its y band + sprite size, its scroll factor (0 = static far, 1 =
	// foreground), and its depth (larger = nearer, so it occludes). Far→near so the instanced order is painterly too.
	private readonly record struct Layer(int Cell, float Y, float Size, float Factor, float Depth, float Spacing);

	private static readonly Layer[] Layers =
	[
		new(Cell: 2, Y: 124f, Size: 26f, Factor: 0.15f, Depth: 1f, Spacing: 58f), // blue  — distant
		new(Cell: 1, Y: 98f,  Size: 30f, Factor: 0.40f, Depth: 2f, Spacing: 66f), // green — mid
		new(Cell: 3, Y: 72f,  Size: 34f, Factor: 0.70f, Depth: 3f, Spacing: 74f), // yellow— near
		new(Cell: 0, Y: 46f,  Size: 40f, Factor: 1.00f, Depth: 4f, Spacing: 84f), // red   — foreground
	];

	private Material _atlas;
	private Vector4[] _cellUvs = [];
	private IEngine _engine = null!;
	private Concepts.Camera _cam = null!;
	private ConstantsHandle _viewproj = null!;

	public string Name => "Parallax";

	public void DefineTechniques(IEngine engine)
	{
		_engine = engine;
		_cam = new Concepts.Camera(engine);
		engine.DefineTechnique("sprite", new TechniqueDescriptor(
			"gbuffer_sprite", HasVertexInput: true, Layout: VertexLayout.MeshStandard, Instance: SandboxVertexLayouts.Sprite));
		engine.DefineTechnique("compose2d", new TechniqueDescriptor("compose2d"));
		engine.DefineTechnique("blit", new TechniqueDescriptor("blit"));

		_atlas = BallAtlas.CreateMaterial(engine, out var sheet);
		_cellUvs = [sheet.Uv(0), sheet.Uv(1), sheet.Uv(2), sheet.Uv(3)];
	}

	public Concepts.Camera? GetCamera(float aspect, float time)
	{
		var eye = new Vector3(WorldWidth * 0.5f, WorldHeight * 0.5f, 10f);
		return _cam.Orthographic(WorldWidth, WorldHeight, 0.1f, 100f).LookAt(eye, eye with { Z = 0f });
	}

	public RenderGraph Build(DemoScene scene) => RenderGraph.Define(g =>
	{
		var screen = g.Backbuffer;
		var world = g.Domain("parallax2d", WorldWidth, WorldHeight);
		var albedo = world.Color("px_albedo", Format.Rgba8Srgb);
		var normal = world.Color("px_normal", Format.Rgba8Unorm);
		var emissive = world.Color("px_emissive", Format.Rgba8Srgb);
		var depth = world.Depth("px_depth", Format.Depth32F);
		var lit = world.Color("px_lit", Format.Rgba16F);
		_viewproj = g.Constants<Matrix4x4>("parallax.viewproj");

		// One instanced draw rasterises every layer's sprites; the depth target resolves occlusion (near over far).
		g.Pass("sprites")
			.Reads(_viewproj)
			.Writes(albedo, normal, emissive)
			.WritesDepth(depth)
			.Draw(ctx =>
			{
				var sprites = BuildLayers(ctx.Time);
				ctx.Draw(new DrawCall { Mesh = scene.Quad, Material = _atlas, Technique = "sprite", Instances = DrawCall.From(sprites.AsSpan()) });
			});

		g.Pass("compose")
			.Samples(albedo, normal, emissive)
			.Writes(lit)
			.Draw(ctx => ctx.FullScreen("compose2d", albedo, normal, emissive));

		g.Pass("upscale")
			.Samples(lit)
			.Writes(screen)
			.Draw(ctx => ctx.FullScreen("blit", lit));

		DemoGraph.Hud(g, screen);
		g.Present(screen);
	});

	// Publish this frame's orthographic view-projection for the sprite pass to read.
	public void Update(Concepts.Camera camera) => _engine.Write(_viewproj, camera.ViewProjection);

	public string HudStatus => $"{Layers.Length} parallax layers (1 depth-ordered draw)";

	// Place each layer's sprites, scrolled by ctx.Time × the layer's parallax factor and wrapped seamlessly, into
	// one instance array. Allocated per frame (cheap) since the instances are CPU-uploaded each frame anyway.
	private SpriteInstance[] BuildLayers(float time)
	{
		var scroll = time * ScrollSpeed;
		var sprites = new List<SpriteInstance>(64);
		foreach (var layer in Layers)
		{
			// A repeating run wide enough to cover the screen + a sprite of margin on each side; offset and wrap.
			var period = WorldWidth + layer.Spacing * 2f;
			var offset = -scroll * layer.Factor;
			var count = (int)MathF.Ceiling(period / layer.Spacing) + 1;
			for (var i = 0; i < count; i++)
			{
				var raw = i * layer.Spacing + offset;
				var x = Wrap(raw, -layer.Spacing, period - layer.Spacing); // [-spacing, WorldWidth+spacing)
				sprites.Add(SpriteInstance.At(new Vector2(x, layer.Y), new Vector2(layer.Size, layer.Size), _cellUvs[layer.Cell], layer.Depth));
			}
		}
		return [.. sprites];
	}

	// Wrap v into [lo, hi) — for seamless horizontal scrolling of a periodic layer.
	private static float Wrap(float v, float lo, float hi)
	{
		var range = hi - lo;
		var t = (v - lo) % range;
		if (t < 0f) t += range;
		return t + lo;
	}
}
