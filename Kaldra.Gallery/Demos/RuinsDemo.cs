using System.Numerics;

using BinaryVibrance.Kaldra.Assets;
using BinaryVibrance.Kaldra.Core.Animation;
using BinaryVibrance.Kaldra.Core.Geometry;
using BinaryVibrance.Kaldra.Core.Graph;
using BinaryVibrance.Kaldra.Core.Hosting;
using BinaryVibrance.Kaldra.Core.Materials;

using BinaryVibrance.Vysara;
using BinaryVibrance.Vysara.Controls;

namespace Kaldra.Gallery.Demos;

/// <summary>
/// Ancient ruins — a stone-courtyard SSAO/shadow showcase built from many repeated Kenney castle-kit pieces. A
/// walled courtyard (rows of walls + a gate arch, four stacked corner towers) encloses a glowing torus summoning
/// circle ringing a floating icosphere artifact, with rocks, stairs and trees scattered through it and a
/// compute-skinned character pacing the stone. Each glTF piece is grounded by its measured bounding box (so
/// nothing floats) and textured from the kit's shared colormap (so it reads as painted stone, not blank grey).
/// The scene renders deferred into a fixed full-res world: shadow → textured g-buffer → SSAO (contact shadows in
/// the dense crevices, the payoff) → sun lighting → sky → the additive emissive ring + artifact → object-id +
/// jump-flood outline (hover the artifact or the character) → bloom → auto-exposure tonemap → a warm-stone grade →
/// blit. Frozen time pins a deterministic cross-backend golden.
/// </summary>
internal sealed class RuinsDemo : IDemo
{
	public string Name => "Ancient Ruins";

	private const float ShadowResolution = 2048f;

	// Loaded-once piece definitions (geometry + measured bounds), reused across every placement so a repeated
	// wall/tower is one upload drawn many times.
	private sealed record PieceDef(Mesh[] Meshes, float MinY, float Height, float Width, float Depth);
	private readonly Dictionary<string, PieceDef> _library = [];
	private readonly List<(Mesh Mesh, Matrix4x4 Model)> _pieces = [];

	// The distinct Kenney castle-kit piece names BuildCourtyard places — preloaded concurrently up front so a
	// repeated piece (e.g. "wall", placed a dozen times) costs one fetch+parse, and different pieces load in
	// parallel rather than one-at-a-time on first use. Keep in sync with BuildCourtyard's Place(...) calls.
	private static readonly string[] PieceNames =
	[
		"wall", "tower-square-arch", "tower-square-base", "tower-square-mid", "tower-square-top",
		"rocks-large", "rocks-small", "stairs-stone", "tree-large",
	];

	private Mesh _ring;
	private Mesh _artifact;
	private Mesh _ground;
	private Matrix4x4 _ringModel;
	private Matrix4x4 _artifactModel;
	private Material _stone; // the kit's shared colormap atlas

	private Mesh _char;
	private int _charVerts;
	private Skeleton _skeleton = null!;
	private AnimationClip[] _clips = [];
	private Matrix4x4[] _palette = [];
	private Matrix4x4 _charModel;

	private IEngine _engine = null!;
	private DemoScene _scene;
	private Concepts.Camera _cam = null!;
	private Concepts.Camera _sunCam = null!;
	private readonly float[] _skyParams = new float[36];

	// The sun direction the lighting + sky share; the sun camera (built in DefineTechniques) frames the whole
	// courtyard, not just the origin, so the far walls/towers still cast into the shadow map.
	private static readonly Vector3 SunDir = Vector3.Normalize(new Vector3(0.5f, 0.72f, -0.45f));

	private readonly DayNight.State _sky = new(
		SunDir,
		new Vector3(1.0f, 0.95f, 0.84f),
		1.9f,
		new Vector3(0.34f, 0.36f, 0.42f),
		new Vector3(0.07f, 0.20f, 0.52f),  // deep zenith blue
		new Vector3(0.24f, 0.42f, 0.72f)); // horizon blue

	private ColorHandle _ids = null!;
	private ConstantsHandle _hoverParams = null!;
	private ConstantsHandle _viewproj = null!;
	private ConstantsHandle _sunViewproj = null!;

	private float _bloomThreshold = 1.5f; // only the emissive ring/artifact bloom — the lit stone shouldn't wash the sky
	private float _gradeStrength = 0.5f;
	private int _clipIdx;

	public void DefineTechniques(IEngine engine)
	{
		_engine = engine;
		_cam = new Concepts.Camera(engine);

		engine.DefineTechnique("skin", TechniqueDescriptor.Compute("skin", hasFragmentPush: true));
		engine.DefineTechnique("depthonly", new TechniqueDescriptor("depthonly", HasVertexInput: true));
		engine.DefineTechnique("gbuffer", new TechniqueDescriptor("gbuffer", HasVertexInput: true));
		engine.DefineTechnique("gbuffer.tex", new TechniqueDescriptor("gbuffer_textured", HasVertexInput: true, LinearFilter: true));
		engine.DefineTechnique("ssao", new TechniqueDescriptor("ssao", HasFragmentPush: true));
		engine.DefineTechnique("ssao.blur", new TechniqueDescriptor("ssao_blur"));
		engine.DefineTechnique("lighting", new TechniqueDescriptor("lighting", HasFragmentPush: true));
		engine.DefineTechnique("sky", new TechniqueDescriptor("sky", Blend: BlendMode.AlphaOver));
		// The ring + artifact reuse emissive.slang as an additive forward technique (its own name so it doesn't
		// collide with other demos' "emissive" — the gallery technique registry is last-wins by name).
		engine.DefineTechnique("ruins.emissive", new TechniqueDescriptor("emissive", HasVertexInput: true, Blend: BlendMode.Additive));
		engine.DefineTechnique("id", new TechniqueDescriptor("id", HasVertexInput: true));
		engine.DefineTechnique("outline.seed", new TechniqueDescriptor("outline_seed"));
		engine.DefineTechnique("outline.jfa", new TechniqueDescriptor("outline_jfa", HasFragmentPush: true));
		engine.DefineTechnique("outline.resolve", new TechniqueDescriptor("outline_resolve"));
		engine.DefineTechnique("bloom.prefilter", new TechniqueDescriptor("bloom_prefilter", LinearFilter: true, HasFragmentPush: true));
		engine.DefineTechnique("bloom.down", new TechniqueDescriptor("bloom_sample", LinearFilter: true));
		engine.DefineTechnique("bloom.up", new TechniqueDescriptor("bloom_sample", Blend: BlendMode.Additive, LinearFilter: true));
		engine.DefineTechnique("bloom.composite", new TechniqueDescriptor("bloom_composite", LinearFilter: true, HasFragmentPush: true));
		engine.DefineTechnique("exposure", TechniqueDescriptor.Compute("exposure", hasFragmentPush: true));
		engine.DefineTechnique("tonemap", new TechniqueDescriptor("tonemap", HasFragmentPush: true));
		engine.DefineTechnique("grade", new TechniqueDescriptor("grade", HasFragmentPush: true));
		engine.DefineTechnique("blit", new TechniqueDescriptor("blit"));
	}

	// Loads every piece the courtyard/character need through the scene resolver — a file read on desktop, an HTTP
	// fetch in the browser — fetched concurrently (all pieces + the colormap + the character), so the demo loads
	// the same way, and considerably faster (parallel instead of serial), on both hosts.
	public async Task PreloadAsync(DemoScene scene)
	{
		_scene = scene;

		var colormapBytesTask = scene.Asset("kenney/castle/Textures/colormap.png");
		var characterBytesTask = scene.Asset("kenney/characters/character_animated.glb");
		var pieceBytesTasks = PieceNames.ToDictionary(name => name, name => scene.Asset($"kenney/castle/{name}.glb"));
		await Task.WhenAll([.. pieceBytesTasks.Values, colormapBytesTask, characterBytesTask]);

		// Parse + upload each piece now that every fetch has resolved. Sequential, not concurrent: every GPU-creating
		// call — including the *Async ones — only ever actually runs on the single thread draining EngineBase's
		// pending-work queue, so awaiting each piece one at a time here doesn't cost real parallelism (the fetches
		// above already overlapped; this part was never the slow part), it just keeps the code simple.
		foreach (var (name, bytesTask) in pieceBytesTasks)
			_library[name] = await BuildPieceAsync(bytesTask.Result);

		BuildCourtyard();

		// The summoning circle (a flat glowing ring) + the floating artifact gem at the centre.
		var (ringV, ringI) = Geometry.BuildTorus(2.6f, 0.16f, 64, 12);
		_ring = await _engine.CreateMeshAsync(ringV, ringI);
		_ringModel = Matrix4x4.CreateScale(0.8f, 0.4f, 0.8f) * Matrix4x4.CreateTranslation(0f, 0.06f, 0f);
		var (artV, artI) = Geometry.BuildIcosphere(3);
		_artifact = await _engine.CreateMeshAsync(artV, artI);
		_artifactModel = Matrix4x4.CreateScale(0.8f) * Matrix4x4.CreateTranslation(0f, 1.7f, 0f);

		// A stone ground plane sized to the courtyard.
		const float s = 18f;
		var up = Vector3.UnitY;
		var t = new Vector4(1, 0, 0, 1);
		_ground = await _engine.CreateMeshAsync(
			[new(new Vector3(-s, 0, -s), up, new Vector2(0, 0), t), new(new Vector3(s, 0, -s), up, new Vector2(1, 0), t),
			 new(new Vector3(s, 0, s), up, new Vector2(1, 1), t), new(new Vector3(-s, 0, s), up, new Vector2(0, 1), t)],
			[0, 1, 2, 0, 2, 3]);

		// The compute-skinned character — the Kenney survivor (mesh + idle/run/jump clips) loaded through the
		// neutral glTF path. Scale it from its measured height to stand a touch shorter than a wall, and ground it.
		var model = GltfLoader.LoadGlb(characterBytesTask.Result);
		var prim = model.Primitives[0];
		_char = await _engine.CreateSkinnedMeshAsync(prim.SkinnedVertices!, prim.Indices);
		_charVerts = prim.SkinnedVertices!.Length;
		_skeleton = model.Skeleton!;
		_clips = [.. model.Clips];
		_palette = new Matrix4x4[_skeleton.JointCount];
		var (cmin, cmax) = Bounds(prim.Vertices);
		// Size the character relative to the real wall height (a head shorter than a wall) rather than an absolute
		// guess, so it stays in proportion to the kit whatever the asset's native scale.
		var wallHeight = _library["wall"].Height;
		var charScale = wallHeight * 0.8f / MathF.Max(cmax.Y - cmin.Y, 0.001f);
		// Stand the character just inside the summoning circle, in the fly-through camera's view, facing the artifact.
		_charModel = Matrix4x4.CreateScale(charScale)
			* Matrix4x4.CreateRotationY(-2.6f)
			* Matrix4x4.CreateTranslation(0.9f, -cmin.Y * charScale, 1.4f);

		// The kit's shared colormap atlas — every castle piece's uvs index a flat palette region of this one image.
		var colormap = await Task.Run(() => ImageLoader.Decode(colormapBytesTask.Result, TextureFormat.Rgba8Srgb));
		var stoneTexture = await _engine.CreateTextureAsync(colormap);
		_stone = await _engine.CreateMaterialAsync(stoneTexture);

		// Frame the whole courtyard from the sun for the shadow pass (a wide ortho centred on the courtyard).
		_sunCam = new Concepts.Camera(_engine)
			.LookAt(SunDir * 26f, new Vector3(0f, 1.5f, 0f))
			.Orthographic(30f, 30f, 0.1f, 52f);
	}

	// Parse an already-fetched castle-kit piece and measure its bounding box (for grounding + footprint-based layout).
	private async Task<PieceDef> BuildPieceAsync(byte[] bytes)
	{
		var loaded = GltfLoader.LoadGlb(bytes);
		var meshes = new List<Mesh>();
		var min = new Vector3(float.MaxValue);
		var max = new Vector3(float.MinValue);
		foreach (var prim in loaded.Primitives)
		{
			meshes.Add(await _engine.CreateMeshAsync(prim.Vertices, prim.Indices));
			var (pmin, pmax) = Bounds(prim.Vertices);
			min = Vector3.Min(min, pmin);
			max = Vector3.Max(max, pmax);
		}
		return new PieceDef([.. meshes], min.Y, max.Y - min.Y, max.X - min.X, max.Z - min.Z);
	}

	// Lay out the courtyard from the measured piece footprint, so rows stay flush and each wall's long face runs
	// ALONG its row (a wall whose long axis is the kit's Z needs a 90° yaw to span an X-row), regardless of the
	// kit's native orientation.
	private void BuildCourtyard()
	{
		var wall = Load("wall");
		var step = MathF.Max(wall.Width, wall.Depth); // a wall tile's footprint → adjacent tiles sit flush
		var longIsX = wall.Width >= wall.Depth;       // is the wall natively long along X?
		var yawAlongX = longIsX ? 90f : 0f;           // a quarter-turn so the wall face sits on the row's edge
		var yawAlongZ = longIsX ? 0f : 90f;           // …for the side rows
		const int span = 3;                           // walls per half-row → a (2·span+1)-wide courtyard
		var half = span * step;                       // courtyard half-extent

		// Back wall (z = -half) with a gate arch at the centre; the corners are left for towers.
		for (var i = -span; i <= span; i++)
		{
			if (i == 0) { Place("tower-square-arch", 0f, -half, yawDeg: yawAlongX); continue; }
			if (Math.Abs(i) == span) continue; // corner → tower
			Place("wall", i * step, -half, yawDeg: yawAlongX);
		}
		// Side walls (x = ±half), long face running front-to-back; corners left for towers.
		for (var j = -span + 1; j <= span - 1; j++)
		{
			Place("wall", -half, j * step, yawDeg: yawAlongZ);
			Place("wall", half, j * step, yawDeg: yawAlongZ);
		}
		// Front wall (z = +half): a broken, ruined edge — every other bay, leaving gaps to fly through.
		for (var i = -span + 1; i <= span - 1; i += 2)
			Place("wall", i * step, half, yawDeg: yawAlongX);

		// Four stacked corner towers (base → mid → top), each tier grounded flush on the one below.
		foreach (var (cx, cz) in new[] { (-half, -half), (half, -half), (-half, half), (half, half) })
		{
			var top = Place("tower-square-base", cx, cz);
			top = Place("tower-square-mid", cx, cz, baseY: top);
			Place("tower-square-top", cx, cz, baseY: top);
		}

		// Rubble, stairs and trees scattered through the yard — more repeated kit, breaking up the floor.
		Place("rocks-large", -half * 0.55f, half * 0.45f, 0.9f, 20f);
		Place("rocks-small", half * 0.5f, half * 0.5f, 1.0f, -35f);
		Place("rocks-small", -half * 0.2f, -half * 0.6f, 0.8f, 60f);
		Place("rocks-large", half * 0.6f, -half * 0.2f, 0.7f, 110f);
		Place("stairs-stone", half * 0.18f, half * 0.62f, 1.0f, 180f);
		Place("tree-large", -half * 0.85f, -half * 0.15f);
		Place("tree-large", half * 0.85f, half * 0.2f, 0.85f);
	}

	// Look up a piece already preloaded into _library by PreloadAsync. A miss means BuildCourtyard placed a name
	// that PieceNames doesn't list — a bug to fail loudly on, not a normal lazy-load path (everything it might
	// place was preloaded, concurrently, before this ever runs).
	private PieceDef Load(string name) => _library.TryGetValue(name, out var def)
		? def
		: throw new InvalidOperationException($"Piece '{name}' was not preloaded — add it to {nameof(PieceNames)}.");

	// Place a piece at (x,z): scale, optional yaw, lift so its measured base sits at baseY. Returns the world-space
	// top (baseY + scaled height) so a caller can stack the next tier flush on top.
	private float Place(string name, float x, float z, float scale = 1f, float yawDeg = 0f, float baseY = 0f)
	{
		var def = Load(name);
		var model = Matrix4x4.CreateScale(scale)
			* Matrix4x4.CreateRotationY(yawDeg * MathF.PI / 180f)
			* Matrix4x4.CreateTranslation(x, baseY - def.MinY * scale, z);
		foreach (var m in def.Meshes)
			_pieces.Add((m, model));
		return baseY + def.Height * scale;
	}

	private static (Vector3 Min, Vector3 Max) Bounds(Vertex[] vertices)
	{
		var min = new Vector3(float.MaxValue);
		var max = new Vector3(float.MinValue);
		foreach (var v in vertices)
		{
			min = Vector3.Min(min, v.Position);
			max = Vector3.Max(max, v.Position);
		}
		return (min, max);
	}

	public Concepts.Camera? GetCamera(float aspect, float time)
	{
		var wall = Load("wall");
		var step = MathF.Max(wall.Width, wall.Depth);
		var half = 3 * step;

		// A looping cubic-bezier fly-through, eased with smoothstep so velocity falls to zero at the loop seam (no
		// jarring bounce): it glides to a soft pause just outside the front, dives through a gap in the broken front
		// wall, sweeps low around the summoning circle (left → right), and rises back out — always looking at the
		// centre, so the arc reads as circling the artifact. P0 == P3 closes the loop seamlessly; the phase is offset
		// so a frozen capture (time == 1) lands mid-flight on the interior sweep past the circle. Kept close so the
		// courtyard fills the frame rather than receding to a distant miniature.
		var phase = (time * (1f / 12f) + 0.417f) % 1f;
		var t = phase * phase * (3f - 2f * phase); // smoothstep ease — zero velocity at phase 0/1 (the seam)
		var p0 = new Vector3(0f, half * 0.55f, half + 2.8f);    // just outside the front, moderate height (loop start = end)
		var p1 = new Vector3(-half * 0.85f, 1.2f, half * 0.4f); // in through a gap, low, by the circle's left
		var p2 = new Vector3(half * 0.9f, 1.3f, -half * 0.55f); // around to the circle's right/back
		var eye = Bezier(p0, p1, p2, p0, t);
		return _cam.Perspective(MathF.PI / 3f, aspect, 0.2f, 120f).LookAt(eye, new Vector3(0f, half * 0.32f, 0f));
	}

	// A cubic bezier through the four control points (degree-3 Bernstein form).
	private static Vector3 Bezier(Vector3 a, Vector3 b, Vector3 c, Vector3 d, float t)
	{
		var u = 1f - t;
		return u * u * u * a + 3f * u * u * t * b + 3f * u * t * t * c + t * t * t * d;
	}

	public void Update(Concepts.Camera camera)
	{
		_cam = camera;
		_engine.Write(_viewproj, camera.ViewProjection);
		_engine.Write(_sunViewproj, _sunCam.ViewProjection);
	}

	private UIElement? _debugPanel;

	public UIElement? DebugPanel => _debugPanel ??= BuildDebugPanel();

	private UIElement BuildDebugPanel()
	{
		var panel = new StackPanel();
		panel.Children.Add(DebugSlider.Labeled("Bloom threshold", _bloomThreshold, 0.2f, 4f, v => _bloomThreshold = v));
		panel.Children.Add(DebugSlider.Labeled("Grade strength", _gradeStrength, 0f, 1f, v => _gradeStrength = v));
		if (_clips.Length > 1)
			panel.Children.Add(DebugSlider.Labeled("Animation clip", _clipIdx, 0, _clips.Length - 1, v => _clipIdx = (int)v));
		return panel;
	}

	public RenderGraph Build(DemoScene scene)
	{
		return RenderGraph.Define(g =>
	{
		var screen = g.Backbuffer;
		var world = g.Domain("ruins", 1280, 720);
		var albedo = world.Color("ruins_albedo", Format.Rgba8Srgb);
		var normal = world.Color("ruins_normal", Format.Rgba16F);
		var position = world.Color("ruins_position", Format.Rgba16F);
		var depth = world.Depth("ruins_depth", Format.Depth32F);
		var ao = world.Color("ruins_ao", Format.R16F);
		var aoBlur = world.Color("ruins_ao_blur", Format.R16F);
		var lit = world.Color("ruins_lit", Format.Rgba16F);
		var ids = world.Color("ruins_id", Format.R16F);
		var seedA = world.Color("ruins_outline_a", Format.Rg16F);
		var seedB = world.Color("ruins_outline_b", Format.Rg16F);
		var outlined = world.Color("ruins_outlined", Format.Rgba16F);
		var toned = world.Color("ruins_toned", Format.Rgba16F);
		var graded = world.Color("ruins_graded", Format.Rgba16F);
		var skinned = g.Buffer("ruins_skinned", _charVerts, VertexLayout.MeshStandard);
		var meter = g.Domain("ruins_meter", 1, 1);
		var exposure = meter.Persistent("ruins_exposure", Format.R32F);

		var sun = g.Domain("ruins_sun", (int)ShadowResolution, (int)ShadowResolution);
		var shadowMap = sun.Depth("ruins_sun_depth", Format.Depth32F);

		_viewproj = g.Constants<Matrix4x4>("ruins.viewproj");
		_sunViewproj = g.Constants<Matrix4x4>("ruins.sun.viewproj");
		_ids = ids;
		_hoverParams = g.Constants<DemoGraph.OutlineParams>("ruins.outline.params");

		// Skin the character once; every depth + g-buffer + id reader uses the result.
		g.Compute("skin")
			.ReadWrites(skinned)
			.Dispatch(ctx =>
			{
				var clip = _clips[Math.Clamp(_clipIdx, 0, _clips.Length - 1)];
				var tt = clip.Duration > 0f ? (float)ctx.Time % clip.Duration : 0f;
				AnimationSampler.Evaluate(_skeleton, clip, tt, _palette);
				ctx.DispatchSkin("skin", _char, _palette, skinned, _charVerts);
			});

		g.Pass("shadow")
			.Reads(_sunViewproj)
			.WritesDepth(shadowMap)
			.ReadsVertex(skinned)
			.Draw(ctx =>
			{
				DrawPieces(ctx, "depthonly", null);
				DemoDraw.Mesh(ctx, _ground, "depthonly", Matrix4x4.Identity);
				DemoDraw.Skinned(ctx, _char, skinned, "depthonly", _charModel);
			});

		g.Pass("gbuffer")
			.Reads(_viewproj)
			.Writes(albedo, normal, position)
			.WritesDepth(depth)
			.ReadsVertex(skinned)
			.Draw(ctx =>
			{
				DrawPieces(ctx, "gbuffer.tex", _stone);
				DemoDraw.Mesh(ctx, _ground, "gbuffer", Matrix4x4.Identity);
				DemoDraw.Skinned(ctx, _char, skinned, "gbuffer", _charModel);
			});

		g.Pass("ssao")
			.Samples(position, normal)
			.Writes(ao)
			.Draw(ctx => ctx.FullScreen("ssao", DemoGraph.Mat(_cam.ViewProjection), position, normal));

		g.Pass("ssao.blur")
			.Samples(ao)
			.Writes(aoBlur)
			.Draw(ctx => ctx.FullScreen("ssao.blur", ao));

		g.Pass("lighting")
			.Samples(albedo, normal, position, shadowMap, aoBlur)
			.Writes(lit)
			.Draw(ctx => ctx.FullScreen("lighting", DemoGraph.Mat(_sunCam.ViewProjection), albedo, normal, position, shadowMap, aoBlur));

		g.Pass("sky").Blends(lit).Samples(depth).Draw(ctx => ctx.FullScreen("sky", PackSky(), depth));

		// The glowing summoning ring + artifact: additive over the lit scene, depth-tested so the stone occludes them.
		g.Pass("artifact")
			.Reads(_viewproj)
			.Blends(lit)
			.ReadsDepth(depth)
			.Draw(ctx =>
			{
				DemoDraw.Mesh(ctx, _ring, "ruins.emissive", _ringModel);
				DemoDraw.Mesh(ctx, _artifact, "ruins.emissive", _artifactModel);
			});

		// Object ids for picking: the artifact (1) and the character (2). Read back so the host knows the hovered id.
		g.Pass("id")
			.Reads(_viewproj)
			.ReadsVertex(skinned)
			.Writes(ids)
			.Draw(ctx =>
			{
				DemoDraw.Mesh(ctx, _artifact, "id", _artifactModel, 1);
				DemoDraw.Skinned(ctx, _char, skinned, "id", _charModel, 2);
			});
		g.Readback(ids);

		g.Pass("outline.seed")
			.Samples(ids)
			.Reads(_hoverParams)
			.Writes(seedA)
			.Draw(ctx => ctx.FullScreen("outline.seed", ids));

		var (read, write) = (seedA, seedB);
		for (var step = 4; step >= 1; step >>= 1)
		{
			var src = read;
			float jump = step;
			g.Pass($"outline.jfa{step}")
				.Samples(src)
				.Writes(write)
				.Draw(ctx => ctx.FullScreen("outline.jfa", [jump, world.Width, world.Height], src));
			(read, write) = (write, read);
		}

		var flood = read;
		g.Pass("outline.resolve")
			.Samples(flood, lit)
			.Writes(outlined)
			.Draw(ctx => ctx.FullScreen("outline.resolve", flood, lit));

		// Bloom (the artifact/ring glow + outline ring), auto-expose to the lit scene, then a warm-stone grade. Many
		// mips at low intensity → a smooth, soft glow (few mips left visible blocky mip steps in the halo).
		var bloomed = g.Bloom(outlined, () => _bloomThreshold, () => 0.3f, mips: 6);

		g.Compute("exposure")
			.Samples(lit)
			.ReadWrites(exposure)
			.Dispatch(ctx => ctx.Dispatch("exposure", (1, 1, 1), [8f * (float)ctx.DeltaTime], [lit], [exposure]));

		g.Pass("tonemap")
			.Samples(bloomed, exposure)
			.Writes(toned)
			.Draw(ctx => ctx.FullScreen("tonemap", [ctx.HdrOutput ? 1f : 0f], bloomed, exposure));

		g.Pass("grade")
			.Samples(toned)
			.Writes(graded)
			.Draw(ctx => ctx.FullScreen("grade", [1.05f, 1.0f, 0.93f, _gradeStrength, 1.12f, 1.0f, 0f, 0f], toned));

		g.Pass("blit").Samples(graded).Writes(screen).Draw(ctx => ctx.FullScreen("blit", graded));
		DemoGraph.Hud(g, screen, scene);
		g.Present(screen);
	});
	}

	// Draw every placed castle piece with the given technique (and optional colormap material for the g-buffer pass).
	private void DrawPieces(IRenderContext ctx, string technique, Material? material)
	{
		foreach (var (mesh, model) in _pieces)
			DemoDraw.Mesh(ctx, mesh, technique, model, 0, material);
	}

	private ReadOnlySpan<float> PackSky()
	{
		var p = _skyParams;
		Matrix4x4.Invert(_cam.ViewProjection, out var invVp);
		System.Runtime.InteropServices.MemoryMarshal.CreateReadOnlySpan(in invVp.M11, 16).CopyTo(p.AsSpan(0, 16));
		Matrix4x4.Invert(_cam.View, out var invView);
		var eye = invView.Translation;
		p[16] = eye.X; p[17] = eye.Y; p[18] = eye.Z; p[19] = 0f;
		p[20] = _sky.SunDir.X; p[21] = _sky.SunDir.Y; p[22] = _sky.SunDir.Z; p[23] = 0f;
		p[24] = _sky.Zenith.X; p[25] = _sky.Zenith.Y; p[26] = _sky.Zenith.Z; p[27] = 0f;
		p[28] = _sky.Horizon.X; p[29] = _sky.Horizon.Y; p[30] = _sky.Horizon.Z; p[31] = 0f;
		p[32] = _sky.SunColor.X; p[33] = _sky.SunColor.Y; p[34] = _sky.SunColor.Z; p[35] = 220f;
		return p;
	}

	public ResourceHandle? PickTarget => _ids;

	public void WriteHover(IEngine engine, uint hoveredId) =>
		engine.Write(_hoverParams, new DemoGraph.OutlineParams(new Vector4(hoveredId, 0f, 0f, 0f)));
}
