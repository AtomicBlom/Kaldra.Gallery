using System.Numerics;

using Loom.Assets;
using Loom.Concepts;
using Loom.Core.Geometry;
using Loom.Core.Graph;
using Loom.Core.Hosting;
using Loom.Core.Materials;

namespace Loom.Sandbox.Demos;

/// <summary>
/// The pixel-art reference demo — the capstone that composes the whole 2D pipeline into one scene: a parallax
/// background, a tile-index tilemap ground, animated sprites, dynamic normal-mapped 2D point lights, and
/// generalized GPU ember particles. The sprites/tilemap rasterise into a single depth-ordered 2D G-buffer,
/// which is composed (ambient + additive light volumes); the particles' textured sub-UV sparks then blend
/// additively over the lit result before it is bloomed and nearest-upscaled from the 320×180 world to the
/// screen. Everything time-driven is pulled from <c>ctx.Time</c> (not captured), so a frozen capture is
/// deterministic — the cross-backend golden.
/// </summary>
internal sealed class PixelArtDemo : IDemo
{
	private const int WorldWidth = 320;
	private const int WorldHeight = 180;
	private const float ScrollSpeed = 14f;

	// 20×12 map (matches tilemap.slang's baked MapSize): a grass-topped ground + two platforms the sprites stand on.
	private static readonly string[] Map =
	[
		"....................",
		"....................",
		"....................",
		"......ggg...........",
		"......ddd...........",
		"............gggg....",
		"............dddd....",
		"....................",
		"gggggggggggggggggggg",
		"dddddddddddddddddddd",
		"ddsssdddddddddsssddd",
		"dddddddddddddddddddd",
	];

	private Material _balls;       // parallax background motifs
	private Material _tiles;       // tilemap atlas + index
	private Mesh _mapQuad;
	private Material _spinners;    // animated sprites
	private Material _sparks;      // textured glow-particle atlas
	private SpriteSheet _ballSheet = new(1, 1);
	private SpriteSheet _spinSheet = new(1, 1);
	private IEngine _engine = null!;
	private ViewConstants _view = null!;

	private const int GlowParticleCount = 512; // multiple of 64 (the compute workgroup size)
	private const int ParticleStrideBytes = 32; // SandboxVertexLayouts.Particle: two IPARTICLE float4 rows

	public string Name => "Pixel Art";

	public void DefineTechniques(IEngine engine)
	{
		_engine = engine;
		engine.DefineTechnique("sprite", new TechniqueDescriptor(
			"gbuffer_sprite", HasVertexInput: true, Layout: VertexLayout.MeshStandard, Instance: SandboxVertexLayouts.Sprite));
		engine.DefineTechnique("tilemap", new TechniqueDescriptor("tilemap", HasVertexInput: true, Layout: VertexLayout.MeshStandard));
		engine.DefineTechnique("ambient2d", new TechniqueDescriptor("ambient2d"));
		engine.DefineTechnique("light2d", new TechniqueDescriptor(
			"light2d", HasVertexInput: true, Blend: BlendMode.Additive, Layout: VertexLayout.MeshStandard, Instance: SandboxVertexLayouts.Light2D));
		engine.DefineTechnique("particle.glow.update", new TechniqueDescriptor(
			"particle_glow_update", VertexEntry: "main", FragmentEntry: "main", IsCompute: true, HasFragmentPush: true));
		engine.DefineTechnique("particle.glow.render", new TechniqueDescriptor(
			"particle_glow_render", Blend: BlendMode.Additive, LinearFilter: true, Instance: SandboxVertexLayouts.Particle));
		engine.DefineTechnique("bloom.prefilter", new TechniqueDescriptor("bloom_prefilter", LinearFilter: true, HasFragmentPush: true));
		engine.DefineTechnique("bloom.down", new TechniqueDescriptor("bloom_sample", LinearFilter: true));
		engine.DefineTechnique("bloom.up", new TechniqueDescriptor("bloom_sample", Blend: BlendMode.Additive, LinearFilter: true));
		engine.DefineTechnique("bloom.composite", new TechniqueDescriptor("bloom_composite", LinearFilter: true, HasFragmentPush: true));
		engine.DefineTechnique("blit", new TechniqueDescriptor("blit"));

		_balls = BallAtlas.CreateMaterial(engine, out _ballSheet);
		_spinners = SpinnerAtlas.CreateMaterial(engine, out _spinSheet);
		_sparks = SparkAtlas.CreateMaterial(engine, out _);

		TileAtlas.CreateTextures(engine, out var atlasAlbedo, out var atlasNormal);
		var index = engine.CreateTexture(TextureData.FromRgba8(20, 12, BuildIndexBytes(), TextureFormat.Rgba8Unorm, generateMips: false));
		_tiles = engine.CreateMaterial(index, atlasAlbedo, atlasNormal);

		// The map quad: the whole world rect at z=0, uv (0,0) at the top-left so map row 0 is the top.
		var n = new Vector3(0, 0, 1);
		var t = new Vector4(1, 0, 0, 1);
		Vertex[] verts =
		[
			new(new Vector3(0, 0, 0), n, new Vector2(0, 1), t),
			new(new Vector3(WorldWidth, 0, 0), n, new Vector2(1, 1), t),
			new(new Vector3(WorldWidth, WorldHeight, 0), n, new Vector2(1, 0), t),
			new(new Vector3(0, WorldHeight, 0), n, new Vector2(0, 0), t),
		];
		_mapQuad = engine.CreateMesh(verts, [0, 1, 2, 0, 2, 3]);
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
		var world = g.Domain("pixelart", WorldWidth, WorldHeight);
		var albedo = world.Color("pa_albedo", Format.Rgba8Srgb);
		var normal = world.Color("pa_normal", Format.Rgba8Unorm);
		var emissive = world.Color("pa_emissive", Format.Rgba8Srgb);
		var depth = world.Depth("pa_depth", Format.Depth32F);
		var lit = world.Color("pa_lit", Format.Rgba16F);
		var glow = g.Buffer("pa_glow", GlowParticleCount * ParticleStrideBytes);
		_view = new ViewConstants(g, "pixelart.viewproj");

		// One depth-ordered G-buffer pass: parallax background (far) → tilemap ground (mid) → animated sprites (near).
		g.Pass("scene")
			.Reads(_view.Handle)
			.Writes(albedo, normal, emissive)
			.WritesDepth(depth)
			.Draw(ctx =>
			{
				ctx.Draw(new DrawCall { Mesh = scene.Quad, Material = _balls, Technique = "sprite", Instances = DrawCall.From(BuildBackground(ctx.Time).AsSpan()) });
				ctx.Draw(new DrawCall { Mesh = _mapQuad, Material = _tiles, Technique = "tilemap" });
				ctx.Draw(new DrawCall { Mesh = scene.Quad, Material = _spinners, Technique = "sprite", Instances = DrawCall.From(BuildSprites(ctx.Time).AsSpan()) });
			});

		// Ambient + emissive base, then the dynamic coloured lights accumulate additively (normal-mapped relief).
		g.Pass("ambient")
			.Samples(albedo, emissive)
			.Writes(lit)
			.Draw(ctx => ctx.FullScreen("ambient2d", albedo, emissive));

		g.Pass("lights")
			.Reads(_view.Handle)
			.Samples(albedo, normal)
			.Blends(lit)
			.Draw(ctx =>
			{
				ReadOnlySpan<ResourceHandle> gbuffer = [albedo, normal];
				ctx.Draw(new DrawCall { Mesh = scene.Quad, Technique = "light2d", Instances = DrawCall.From(BuildLights(ctx.Time).AsSpan()), Sampled = gbuffer });
			});

		// Generalized GPU particles: a compute fountain writes the wide particle stream (the demo pushes the
		// emitter params), then the textured render pass blends warm sub-UV ember sparks additively over lit so they
		// bloom. Two edges — .ReadWrites then .ReadsVertex — and the engine derives the compute→vertex-input barrier.
		g.Compute("particle.glow.update")
			.ReadWrites(glow)
			.Dispatch(ctx => ctx.Dispatch("particle.glow.update", ((uint)(GlowParticleCount / 64), 1, 1), GlowEmitter(ctx.Time), [], [glow]));

		g.Pass("glow")
			.Blends(lit)
			.ReadsVertex(glow)
			.Draw(ctx => ctx.Draw(new DrawCall { Technique = "particle.glow.render", Material = _sparks, VertexCount = 6, Instances = DrawCall.Gpu(glow, GlowParticleCount) })); // 6 = a billboard quad fabricated per spark

		// Bloom the HDR lit result (the light highlights + ember sparks glow), then nearest-upscale to the screen.
		var bloomed = g.Bloom(lit, threshold: 1.0f, mips: 5, intensity: 0.8f);

		g.Pass("upscale")
			.Samples(bloomed)
			.Writes(screen)
			.Draw(ctx => ctx.FullScreen("blit", bloomed));

		DemoGraph.Hud(g, screen);
		g.Present(screen);
	});

	// Publish this frame's orthographic view-projection for the scene + light passes to read. (The glow particle
	// pass still rides the engine's auto-pushed view-projection — it isn't on the constants path.)
	public void Update(Camera camera) => _view.Publish(_engine, camera);

	public string HudStatus => "pixel-art reference: parallax + tilemap + anim + 2D lights + bloom";

	// Two far parallax layers of dim, desaturated balls drifting across the sky, behind the tilemap (z < 0).
	private SpriteInstance[] BuildBackground(float time)
	{
		var sprites = new List<SpriteInstance>(24);
		(int cell, float y, float size, float factor, float depth, float spacing, Vector4 tint)[] layers =
		[
			(2, 150f, 52f, 0.10f, -8f, 90f, new Vector4(0.30f, 0.34f, 0.55f, 1f)), // distant blue band
			(3, 128f, 40f, 0.22f, -6f, 74f, new Vector4(0.42f, 0.40f, 0.62f, 1f)), // nearer purple band
		];
		var scroll = time * ScrollSpeed;
		foreach (var (cell, y, size, factor, depth, spacing, tint) in layers)
		{
			var period = WorldWidth + spacing * 2f;
			var count = (int)MathF.Ceiling(period / spacing) + 1;
			for (var i = 0; i < count; i++)
			{
				var x = Wrap(i * spacing - scroll * factor, -spacing, period - spacing);
				sprites.Add(SpriteInstance.At(new Vector2(x, y), new Vector2(size, size), _ballSheet.Uv(cell), depth) with { Tint = tint });
			}
		}
		return [.. sprites];
	}

	// A few animated spinners standing on the ground / platforms (near layer, z > 0). Frame advances from time.
	private SpriteInstance[] BuildSprites(float time)
	{
		var frame = (int)(time * 6f);
		(float x, float y)[] spots = [(64f, 70f), (150f, 70f), (250f, 70f), (122f, 146f), (232f, 116f)];
		var sprites = new SpriteInstance[spots.Length];
		for (var i = 0; i < spots.Length; i++)
			sprites[i] = SpriteInstance.At(new Vector2(spots[i].x, spots[i].y), new Vector2(24f, 24f), _spinSheet.Uv(frame + i), depth: 3f);
		return sprites;
	}

	// Three dynamic coloured lights orbiting gently over the scene — the normal-mapped relief read.
	private static Light2DInstance[] BuildLights(float t)
	{
		return
		[
			Light2DInstance.At(new Vector2(160f, 140f) + Orbit(t * 0.35f, 36f), 150f, new Vector3(1.00f, 0.62f, 0.34f), 1.7f, 52f),
			Light2DInstance.At(new Vector2(70f, 88f) + Orbit(t * -0.5f + 1.5f, 28f), 118f, new Vector3(0.40f, 0.62f, 1.00f), 1.3f, 40f),
			Light2DInstance.At(new Vector2(252f, 80f) + Orbit(t * 0.6f + 3.0f, 30f), 118f, new Vector3(0.50f, 0.95f, 0.55f), 1.2f, 40f),
		];
	}

	// The ember emitter, as the generalized particle compute's push block (3 float4s; see particle_glow_update.slang).
	// Rising warm motes across the lower scene — the demo owns the emitter shape, the shader stays generic.
	//   p0: time, lifetime, origin.x, origin.y | p1: spawn half-width, rise, drift, gravity | p2: swirl amp, swirl freq, origin.z, _
	private static float[] GlowEmitter(float time) =>
	[
		time, 3.4f, 160f, 34f,
		150f, 26f, 6f, -3f,
		8f, 1.5f, 3.5f, 0f,
	];

	private static Vector2 Orbit(float angle, float radius) => new(MathF.Cos(angle) * radius, MathF.Sin(angle) * radius);

	private static float Wrap(float v, float lo, float hi)
	{
		var range = hi - lo;
		var x = (v - lo) % range;
		if (x < 0f) x += range;
		return x + lo;
	}

	private static byte[] BuildIndexBytes()
	{
		var bytes = new byte[20 * 12 * 4];
		for (var row = 0; row < 12; row++)
			for (var col = 0; col < 20; col++)
			{
				var id = Map[row][col] switch { 'g' => (byte)1, 'd' => (byte)2, 's' => (byte)3, _ => (byte)0 };
				var o = (row * 20 + col) * 4;
				bytes[o] = id;
				bytes[o + 3] = 255;
			}
		return bytes;
	}
}
