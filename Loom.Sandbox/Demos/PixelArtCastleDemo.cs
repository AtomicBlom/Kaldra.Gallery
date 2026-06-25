using System.Numerics;

using Loom.Assets;
using Loom.Core.Geometry;
using Loom.Core.Graph;
using Loom.Core.Hosting;
using Loom.Core.Materials;

namespace Loom.Sandbox.Demos;

/// <summary>
/// Pixel-art castle — a 2D night-castle scene with a deliberately pixelated, cel-shaded 3D boss composited into it.
/// The 2D half is a parallax castle skyline + moon, a stone rampart tilemap (battlemented brick wall over a
/// flagstone floor), patrolling knight guards and warm torch lights, composed + bloomed + upscaled. The headline is
/// the boss: a 3D golem (icosphere head + capsule body + cylinder arms) renders forward with a toon/cel shader into
/// its own tiny 128×128 domain, and a composite pass nearest-samples that little render into a screen-space
/// rectangle of the 2D scene — so the chunky cel-shaded geometry reads as a hand-placed pixel sprite. It's the
/// low-res-domain feature in a compositing role; the engine derives the cross-domain shader-read transition.
/// </summary>
internal sealed class PixelArtCastleDemo : IDemo
{
	private const int WorldWidth = 320;
	private const int WorldHeight = 180;
	private const int BossSize = 128;
	private const float ScrollSpeed = 10f;

	// A 20×12 rampart: empty sky (the parallax skyline shows through), a battlemented brick wall, then a flagstone
	// floor the guards patrol. Char → tile id in BuildIndexBytes: b=brick, p=pale stone, s=stone, '.'=empty.
	private static readonly string[] Map =
	[
		"....................",
		"....................",
		"....................",
		"....................",
		"....................",
		"....................",
		"b.b.b.b.b.b.b.b.b.b.",
		"bbbbbbbbbbbbbbbbbbbb",
		"pppppppppppppppppppp",
		"ssssssssssssssssssss",
		"ssssssssssssssssssss",
		"ssssssssssssssssssss",
	];

	private Material _back;
	private Material _guards;
	private Material _tiles;
	private Mesh _mapQuad;
	private SpriteSheet _backSheet = new(1, 1);
	private SpriteSheet _guardSheet = new(1, 1);

	private Mesh _head;
	private Mesh _body;
	private Mesh _arm;

	private IEngine _engine = null!;
	private Concepts.Camera _cam = null!;
	private Concepts.Camera _bossCam = null!;
	private ConstantsHandle _viewproj = null!;
	private ConstantsHandle _bossViewproj = null!;

	private float _bossScale = 1f;
	private float _guardSpeed = 1.5f;
	private float _aspect = WorldWidth / (float)WorldHeight; // the live framebuffer aspect, for the composite letterbox

	public string Name => "Pixel Art Castle";

	public void DefineTechniques(IEngine engine)
	{
		_engine = engine;
		_cam = new Concepts.Camera(engine);
		_bossCam = new Concepts.Camera(engine);

		engine.DefineTechnique("sprite", new TechniqueDescriptor(
			"gbuffer_sprite", HasVertexInput: true, Layout: VertexLayout.MeshStandard, Instance: SandboxVertexLayouts.Sprite));
		engine.DefineTechnique("tilemap", new TechniqueDescriptor("tilemap", HasVertexInput: true, Layout: VertexLayout.MeshStandard));
		engine.DefineTechnique("ambient2d", new TechniqueDescriptor("ambient2d"));
		engine.DefineTechnique("light2d", new TechniqueDescriptor(
			"light2d", HasVertexInput: true, Blend: BlendMode.Additive, Layout: VertexLayout.MeshStandard, Instance: SandboxVertexLayouts.Light2D));
		engine.DefineTechnique("toon", new TechniqueDescriptor("toon", HasVertexInput: true)); // the 3D boss (forward cel)
		engine.DefineTechnique("bloom.prefilter", new TechniqueDescriptor("bloom_prefilter", LinearFilter: true, HasFragmentPush: true));
		engine.DefineTechnique("bloom.down", new TechniqueDescriptor("bloom_sample", LinearFilter: true));
		engine.DefineTechnique("bloom.up", new TechniqueDescriptor("bloom_sample", Blend: BlendMode.Additive, LinearFilter: true));
		engine.DefineTechnique("bloom.composite", new TechniqueDescriptor("bloom_composite", LinearFilter: true, HasFragmentPush: true));
		engine.DefineTechnique("blit", new TechniqueDescriptor("blit"));
		engine.DefineTechnique("pixel.composite", new TechniqueDescriptor("pixel_composite", HasFragmentPush: true)); // nearest → the boss stays chunky

		_back = CastleBackdrop.CreateMaterial(engine, out _backSheet);
		_guards = GuardKnight.CreateMaterial(engine, out _guardSheet);

		TileAtlas.CreateTextures(engine, out var atlasAlbedo, out var atlasNormal);
		var index = engine.CreateTexture(TextureData.FromRgba8(20, 12, BuildIndexBytes(), TextureFormat.Rgba8Unorm, generateMips: false));
		_tiles = engine.CreateMaterial(index, atlasAlbedo, atlasNormal);

		// The whole-world map quad (uv (0,0) top-left so map row 0 is the top).
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

		// The 3D boss golem parts (object space, feet at y≈0). Composited cel-shaded + pixelated into the 2D scene.
		var (headV, headI) = Geometry.BuildIcosphere(2);
		_head = engine.CreateMesh(headV, headI);
		var (bodyV, bodyI) = Geometry.BuildCapsule(0.8f, 0.35f, 16, 8);
		_body = engine.CreateMesh(bodyV, bodyI);
		var (armV, armI) = Geometry.BuildCylinder(0.7f, 0.12f, 8);
		_arm = engine.CreateMesh(armV, armI);

		// A fixed perspective view of the golem (square aspect — it renders into a square domain), framed tight so the
		// figure fills the tile with its feet near the bottom edge (so it grounds, not floats, when composited).
		_bossCam.Perspective(0.72f, 1f, 0.1f, 20f).LookAt(new Vector3(0f, 1.0f, 3.2f), new Vector3(0f, 1.0f, 0f));
	}

	public Concepts.Camera? GetCamera(float aspect, float time)
	{
		_aspect = aspect; // stash the framebuffer aspect so the composite can letterbox the low-res scene
		// The 2D scene's orthographic camera (world-pixel space; the floor scrolls subtly via parallax in the sprites).
		var eye = new Vector3(WorldWidth * 0.5f, WorldHeight * 0.5f, 10f);
		return _cam.Orthographic(WorldWidth, WorldHeight, 0.1f, 100f).LookAt(eye, eye with { Z = 0f });
	}

	public void Update(Concepts.Camera camera)
	{
		_engine.Write(_viewproj, camera.ViewProjection);
		_engine.Write(_bossViewproj, _bossCam.ViewProjection);
	}

	public void DrawDebugUI()
	{
		ImGuiNET.ImGui.SliderFloat("Boss scale", ref _bossScale, 0.5f, 2f);
		ImGuiNET.ImGui.SliderFloat("Guard speed", ref _guardSpeed, 0.2f, 4f);
	}

	public RenderGraph Build(DemoScene scene) => RenderGraph.Define(g =>
	{
		var screen = g.Backbuffer;
		var world = g.Domain("pac", WorldWidth, WorldHeight);
		var albedo = world.Color("pac_albedo", Format.Rgba8Srgb);
		var normal = world.Color("pac_normal", Format.Rgba8Unorm);
		var emissive = world.Color("pac_emissive", Format.Rgba8Srgb);
		var depth = world.Depth("pac_depth", Format.Depth32F);
		var lit = world.Color("pac_lit", Format.Rgba16F);

		var boss = g.Domain("pac_boss", BossSize, BossSize);
		var bossColor = boss.Color("pac_boss_color", Format.Rgba16F);
		var bossDepth = boss.Depth("pac_boss_depth", Format.Depth32F);

		_viewproj = g.Constants<Matrix4x4>("pac.viewproj");
		_bossViewproj = g.Constants<Matrix4x4>("pac.boss.viewproj");

		// One depth-ordered 2D G-buffer pass: parallax skyline → rampart tilemap → patrolling guard sprites.
		g.Pass("scene")
			.Reads(_viewproj)
			.Writes(albedo, normal, emissive)
			.WritesDepth(depth)
			.Draw(ctx =>
			{
				ctx.Draw(new DrawCall { Mesh = scene.Quad, Material = _back, Technique = "sprite", Instances = DrawCall.From(BuildBackground(ctx.Time).AsSpan()) });
				ctx.Draw(new DrawCall { Mesh = _mapQuad, Material = _tiles, Technique = "tilemap" });
				ctx.Draw(new DrawCall { Mesh = scene.Quad, Material = _guards, Technique = "sprite", Instances = DrawCall.From(BuildGuards(ctx.Time).AsSpan()) });
			});

		g.Pass("ambient")
			.Samples(albedo, emissive)
			.Writes(lit)
			.Draw(ctx => ctx.FullScreen("ambient2d", albedo, emissive));

		g.Pass("lights")
			.Reads(_viewproj)
			.Samples(albedo, normal)
			.Blends(lit)
			.Draw(ctx =>
			{
				ReadOnlySpan<ResourceHandle> gbuffer = [albedo, normal];
				ctx.Draw(new DrawCall { Mesh = scene.Quad, Technique = "light2d", Instances = DrawCall.From(BuildTorches(ctx.Time).AsSpan()), Sampled = gbuffer });
			});

		// Bloom the lit 2D scene (torch glow). The composite below scales it to the screen (aspect-preserving,
		// nearest), so no separate upscale pass is needed.
		var bloomed = g.Bloom(lit, threshold: 1.0f, mips: 5, intensity: 0.8f);

		// The 3D boss: a turntable golem rendered forward, cel-shaded, into its own tiny domain (depth → coverage).
		g.Pass("boss")
			.Reads(_bossViewproj)
			.Writes(bossColor)
			.WritesDepth(bossDepth)
			.Draw(ctx =>
			{
				// A stocky golem standing on the floor (feet at y≈0): two legs, a wide body, a big head, two thick arms.
				var spin = Matrix4x4.CreateRotationY(ctx.Time * 0.6f);
				var s = _bossScale;
				DemoDraw.Mesh(ctx, _arm, "toon", Matrix4x4.CreateScale(1.7f * s, 0.9f * s, 1.7f * s) * Matrix4x4.CreateTranslation(-0.26f * s, 0.30f * s, 0f) * spin);
				DemoDraw.Mesh(ctx, _arm, "toon", Matrix4x4.CreateScale(1.7f * s, 0.9f * s, 1.7f * s) * Matrix4x4.CreateTranslation(0.26f * s, 0.30f * s, 0f) * spin);
				DemoDraw.Mesh(ctx, _body, "toon", Matrix4x4.CreateScale(1.45f * s, 1.0f * s, 1.2f * s) * Matrix4x4.CreateTranslation(0f, 0.98f * s, 0f) * spin);
				DemoDraw.Mesh(ctx, _head, "toon", Matrix4x4.CreateScale(0.62f * s) * Matrix4x4.CreateTranslation(0f, 1.85f * s, 0f) * spin);
				DemoDraw.Mesh(ctx, _arm, "toon", Matrix4x4.CreateScale(1.5f * s, 1.0f * s, 1.5f * s) * Matrix4x4.CreateRotationZ(0.95f) * Matrix4x4.CreateTranslation(0.6f * s, 1.25f * s, 0f) * spin);
				DemoDraw.Mesh(ctx, _arm, "toon", Matrix4x4.CreateScale(1.5f * s, 1.0f * s, 1.5f * s) * Matrix4x4.CreateRotationZ(-0.95f) * Matrix4x4.CreateTranslation(-0.6f * s, 1.25f * s, 0f) * spin);
			});

		// Composite the aspect-fit 2D scene + the boss to the backbuffer. The boss rect is in CONTENT uv and square
		// in that space (so the 128² render isn't stretched), with its bottom near the floor so the golem stands on
		// the flagstones; the shader letterboxes the 2D scene to the live framebuffer aspect (uniform pixel scale).
		g.Pass("composite")
			.Samples(bloomed, bossColor, bossDepth)
			.Writes(screen)
			.Draw(ctx => ctx.FullScreen("pixel.composite", [0.37f, 0.30f, 0.63f, 0.86f, _aspect, WorldWidth / (float)WorldHeight, 0f, 0f], bloomed, bossColor, bossDepth));

		DemoGraph.Hud(g, screen);
		g.Present(screen);
	});

	public string HudStatus => "pixel-art 2D castle + a cel-shaded 3D boss pixelated through a 128² domain";

	// A parallax castle skyline (towers + a moon) drifting slowly behind the rampart (z < 0 → behind the wall,
	// showing through the empty sky rows of the tilemap).
	private SpriteInstance[] BuildBackground(float time)
	{
		var sprites = new List<SpriteInstance>(12);

		// The moon, fixed high in the sky.
		sprites.Add(SpriteInstance.At(new Vector2(258f, 150f), new Vector2(40f, 40f), _backSheet.Uv(CastleBackdrop.Moon), depth: -9f)
			with { Tint = new Vector4(1f, 1f, 0.96f, 1f) });

		// A row of moonlit tower silhouettes along the back, gently parallax-scrolling.
		(int cell, float size, float y)[] line =
		[
			(CastleBackdrop.Wall, 56f, 112f),
			(CastleBackdrop.Tower, 52f, 116f),
			(CastleBackdrop.Turret, 62f, 122f),
			(CastleBackdrop.Tower, 48f, 114f),
			(CastleBackdrop.Wall, 56f, 112f),
			(CastleBackdrop.Turret, 60f, 120f),
			(CastleBackdrop.Tower, 50f, 116f),
		];
		const float spacing = 52f;
		var period = WorldWidth + spacing * 2f;
		// Snap the parallax scroll to whole world-pixels — sub-pixel motion makes the nearest-sampled silhouettes
		// shimmer/throb in apparent size as they cross texel boundaries.
		var scroll = MathF.Floor(time * ScrollSpeed * 0.15f);
		var tint = new Vector4(0.55f, 0.62f, 0.85f, 1f); // moonlit silhouette
		for (var i = 0; i < line.Length; i++)
		{
			var x = MathF.Floor(Wrap(i * spacing - scroll, -spacing, period - spacing));
			sprites.Add(SpriteInstance.At(new Vector2(x, line[i].y), new Vector2(line[i].size, line[i].size), _backSheet.Uv(line[i].cell), depth: -8f)
				with { Tint = tint });
		}
		return [.. sprites];
	}

	// Knight guards patrolling the flagstone floor; the walk frame advances from time.
	private SpriteInstance[] BuildGuards(float time)
	{
		var frame = (int)(time * 6f) % GuardKnight.Frames;
		(float baseX, float y, float range, float phase)[] spots = [(80f, 54f, 34f, 0f), (220f, 54f, 28f, 2f)];
		var sprites = new SpriteInstance[spots.Length];
		for (var i = 0; i < spots.Length; i++)
		{
			var x = MathF.Floor(spots[i].baseX + MathF.Sin(time * _guardSpeed + spots[i].phase) * spots[i].range);
			sprites[i] = SpriteInstance.At(new Vector2(x, spots[i].y), new Vector2(22f, 30f), _guardSheet.Uv(frame), depth: 3f);
		}
		return sprites;
	}

	// Warm torch lights flickering along the rampart wall — the normal-mapped relief read.
	private static Light2DInstance[] BuildTorches(float t)
	{
		var flickerA = 1.4f + MathF.Sin(t * 11f) * 0.2f;
		var flickerB = 1.3f + MathF.Sin(t * 9f + 1.7f) * 0.2f;
		return
		[
			Light2DInstance.At(new Vector2(60f, 70f), 80f, new Vector3(1.0f, 0.6f, 0.3f), flickerA, 30f),
			Light2DInstance.At(new Vector2(260f, 70f), 80f, new Vector3(1.0f, 0.6f, 0.3f), flickerB, 30f),
			Light2DInstance.At(new Vector2(160f, 40f), 70f, new Vector3(0.5f, 0.7f, 1.0f), 1.0f, 28f),
		];
	}

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
				var id = Map[row][col] switch { 'b' => (byte)4, 'p' => (byte)15, 's' => (byte)3, _ => (byte)0 };
				var o = (row * 20 + col) * 4;
				bytes[o] = id;
				bytes[o + 3] = 255;
			}
		return bytes;
	}
}
