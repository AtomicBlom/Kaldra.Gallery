using System.Numerics;

using Loom.Assets;
using Loom.Core.Geometry;
using Loom.Core.Graph;
using Loom.Core.Hosting;
using Loom.Core.Materials;

namespace Loom.Sandbox.Demos;

/// <summary>
/// Pixel-art castle — a 2D scene with a deliberately pixelated 3D boss composited into it. The 2D half is a
/// parallax background, a stone tilemap floor, animated guard sprites and dynamic 2D point lights, composed +
/// bloomed + upscaled (the M14 pixel pipeline). The headline is the boss: a 3D "golem" (icosphere head + capsule
/// body + cylinder arms) renders into its own tiny 128×128 domain, and a composite pass nearest-samples that
/// little render into a screen-space rectangle of the 2D scene — so the 3D geometry reads as chunky pixels at home
/// in the art. It's the low-res-domain feature in a compositing role; the engine derives the cross-domain
/// shader-read transition because the boss colour/depth become sampled inputs to the screen-domain composite.
/// </summary>
internal sealed class PixelArtCastleDemo : IDemo
{
	private const int WorldWidth = 320;
	private const int WorldHeight = 180;
	private const int BossSize = 128;
	private const float ScrollSpeed = 10f;

	// A 20×12 stone-castle floor (matches tilemap.slang's baked MapSize): a flagstone floor with a raised ledge.
	private static readonly string[] Map =
	[
		"....................",
		"....................",
		"....................",
		"....................",
		"....................",
		"....................",
		"..gg............gg..",
		"..dd............dd..",
		"gggggggggggggggggggg",
		"dddddddddddddddddddd",
		"ddsssddddddddssssddd",
		"dddddddddddddddddddd",
	];

	private Material _back;
	private Material _tiles;
	private Material _guards;
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
		engine.DefineTechnique("lit", new TechniqueDescriptor("lit", HasVertexInput: true)); // the 3D boss (forward)
		engine.DefineTechnique("bloom.prefilter", new TechniqueDescriptor("bloom_prefilter", LinearFilter: true, HasFragmentPush: true));
		engine.DefineTechnique("bloom.down", new TechniqueDescriptor("bloom_sample", LinearFilter: true));
		engine.DefineTechnique("bloom.up", new TechniqueDescriptor("bloom_sample", Blend: BlendMode.Additive, LinearFilter: true));
		engine.DefineTechnique("bloom.composite", new TechniqueDescriptor("bloom_composite", LinearFilter: true, HasFragmentPush: true));
		engine.DefineTechnique("blit", new TechniqueDescriptor("blit"));
		engine.DefineTechnique("pixel.composite", new TechniqueDescriptor("pixel_composite", HasFragmentPush: true)); // nearest → the boss stays chunky

		_back = BallAtlas.CreateMaterial(engine, out _backSheet);
		_guards = SpinnerAtlas.CreateMaterial(engine, out _guardSheet);

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

		// The 3D boss golem parts (object space, feet at y≈0). Composited pixelated into the 2D scene.
		var (headV, headI) = Geometry.BuildIcosphere(2);
		_head = engine.CreateMesh(headV, headI);
		var (bodyV, bodyI) = Geometry.BuildCapsule(0.8f, 0.35f, 16, 8);
		_body = engine.CreateMesh(bodyV, bodyI);
		var (armV, armI) = Geometry.BuildCylinder(0.7f, 0.12f, 8);
		_arm = engine.CreateMesh(armV, armI);

		// A fixed perspective view of the golem (square aspect — it renders into a square domain).
		_bossCam.Perspective(MathF.PI / 3.2f, 1f, 0.1f, 20f).LookAt(new Vector3(0f, 0.95f, 3.4f), new Vector3(0f, 0.85f, 0f));
	}

	public Concepts.Camera? GetCamera(float aspect, float time)
	{
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
		var scene2d = g.Screen.Color("pac_scene2d", Format.Rgba16F);

		var boss = g.Domain("pac_boss", BossSize, BossSize);
		var bossColor = boss.Color("pac_boss_color", Format.Rgba16F);
		var bossDepth = boss.Depth("pac_boss_depth", Format.Depth32F);

		_viewproj = g.Constants<Matrix4x4>("pac.viewproj");
		_bossViewproj = g.Constants<Matrix4x4>("pac.boss.viewproj");

		// One depth-ordered 2D G-buffer pass: parallax background → tilemap floor → animated guard sprites.
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

		// Bloom the lit 2D scene (torch glow), then nearest-upscale the 320×180 world to the full screen.
		var bloomed = g.Bloom(lit, threshold: 1.0f, mips: 5, intensity: 0.8f);
		g.Pass("upscale").Samples(bloomed).Writes(scene2d).Draw(ctx => ctx.FullScreen("blit", bloomed));

		// The 3D boss: a turntable golem rendered forward into its own tiny domain (its own depth → coverage).
		g.Pass("boss")
			.Reads(_bossViewproj)
			.Writes(bossColor)
			.WritesDepth(bossDepth)
			.Draw(ctx =>
			{
				var spin = Matrix4x4.CreateRotationY(ctx.Time * 0.7f);
				var s = _bossScale;
				DemoDraw.Mesh(ctx, _body, "lit", Matrix4x4.CreateScale(s) * Matrix4x4.CreateTranslation(0f, 0.85f * s, 0f) * spin);
				DemoDraw.Mesh(ctx, _head, "lit", Matrix4x4.CreateScale(0.7f * s) * Matrix4x4.CreateTranslation(0f, 1.5f * s, 0f) * spin);
				DemoDraw.Mesh(ctx, _arm, "lit", Matrix4x4.CreateScale(s) * Matrix4x4.CreateRotationZ(0.4f) * Matrix4x4.CreateTranslation(0.45f * s, 0.9f * s, 0f) * spin);
				DemoDraw.Mesh(ctx, _arm, "lit", Matrix4x4.CreateScale(s) * Matrix4x4.CreateRotationZ(-0.4f) * Matrix4x4.CreateTranslation(-0.45f * s, 0.9f * s, 0f) * spin);
			});

		// Composite the boss into a near-square screen rect, nearest-sampled so it reads as chunky pixels.
		g.Pass("composite")
			.Samples(scene2d, bossColor, bossDepth)
			.Writes(screen)
			.Draw(ctx => ctx.FullScreen("pixel.composite", [0.37f, 0.20f, 0.63f, 0.86f], scene2d, bossColor, bossDepth));

		DemoGraph.Hud(g, screen);
		g.Present(screen);
	});

	public string HudStatus => "pixel-art 2D scene + a 3D boss pixelated through a 128² domain";

	// Two far parallax bands of dim motifs drifting behind the floor (z < 0) — distant castle backdrop.
	private SpriteInstance[] BuildBackground(float time)
	{
		var sprites = new List<SpriteInstance>(24);
		(int cell, float y, float size, float factor, float depth, float spacing, Vector4 tint)[] layers =
		[
			(2, 150f, 60f, 0.08f, -8f, 96f, new Vector4(0.22f, 0.24f, 0.40f, 1f)),
			(3, 120f, 46f, 0.18f, -6f, 80f, new Vector4(0.32f, 0.30f, 0.48f, 1f)),
		];
		var scroll = time * ScrollSpeed;
		foreach (var (cell, y, size, factor, depth, spacing, tint) in layers)
		{
			var period = WorldWidth + spacing * 2f;
			var count = (int)MathF.Ceiling(period / spacing) + 1;
			for (var i = 0; i < count; i++)
			{
				var x = Wrap(i * spacing - scroll * factor, -spacing, period - spacing);
				sprites.Add(SpriteInstance.At(new Vector2(x, y), new Vector2(size, size), _backSheet.Uv(cell), depth) with { Tint = tint });
			}
		}
		return [.. sprites];
	}

	// Animated guard sprites pacing the floor (near layer, z > 0); the frame advances from time.
	private SpriteInstance[] BuildGuards(float time)
	{
		var frame = (int)(time * 6f);
		(float baseX, float y, float range, float phase)[] spots = [(80f, 64f, 30f, 0f), (210f, 64f, 26f, 2f)];
		var sprites = new SpriteInstance[spots.Length];
		for (var i = 0; i < spots.Length; i++)
		{
			var x = spots[i].baseX + MathF.Sin(time * _guardSpeed + spots[i].phase) * spots[i].range;
			sprites[i] = SpriteInstance.At(new Vector2(x, spots[i].y), new Vector2(28f, 28f), _guardSheet.Uv(frame + i), depth: 3f);
		}
		return sprites;
	}

	// Warm torch lights flickering along the floor — the normal-mapped relief read.
	private static Light2DInstance[] BuildTorches(float t)
	{
		var flickerA = 1.4f + MathF.Sin(t * 11f) * 0.2f;
		var flickerB = 1.3f + MathF.Sin(t * 9f + 1.7f) * 0.2f;
		return
		[
			Light2DInstance.At(new Vector2(48f, 96f), 90f, new Vector3(1.0f, 0.6f, 0.3f), flickerA, 40f),
			Light2DInstance.At(new Vector2(272f, 96f), 90f, new Vector3(1.0f, 0.6f, 0.3f), flickerB, 40f),
			Light2DInstance.At(new Vector2(160f, 120f), 70f, new Vector3(0.5f, 0.7f, 1.0f), 1.0f, 36f),
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
				var id = Map[row][col] switch { 'g' => (byte)1, 'd' => (byte)2, 's' => (byte)3, _ => (byte)0 };
				var o = (row * 20 + col) * 4;
				bytes[o] = id;
				bytes[o + 3] = 255;
			}
		return bytes;
	}
}
