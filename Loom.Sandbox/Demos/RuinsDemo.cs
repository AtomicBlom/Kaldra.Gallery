using System.Numerics;

using Loom.Assets;
using Loom.Core.Animation;
using Loom.Core.Geometry;
using Loom.Core.Graph;
using Loom.Core.Hosting;
using Loom.Core.Materials;

namespace Loom.Sandbox.Demos;

/// <summary>
/// Ancient ruins — a stone-courtyard SSAO/shadow showcase. A gateway of Kenney castle-kit walls + an arch, a
/// stacked square tower, scattered rocks and a tree are assembled from glTF pieces (each placed by a per-draw
/// model matrix); a torus "summoning circle" rings a glowing icosphere artifact floating at the centre, and a
/// compute-skinned character paces the courtyard. The whole scene renders deferred into a fixed full-res world:
/// shadow → g-buffer → SSAO (contact shadows in the dense stone crevices, the payoff) → sun lighting → sky →
/// the additive emissive artifact → object-id + jump-flood outline (hover the artifact or the character) → bloom
/// → auto-exposure tonemap → a desaturated-stone grade → blit. Frozen time pins a deterministic cross-backend golden.
/// </summary>
internal sealed class RuinsDemo : IDemo
{
	public string Name => "Ancient Ruins";

	private const float ShadowResolution = 2048f;

	private readonly List<(Mesh Mesh, Matrix4x4 Model)> _pieces = [];
	private Mesh _torus;
	private Mesh _artifact;
	private Mesh _ground;
	private Matrix4x4 _torusModel;
	private Matrix4x4 _artifactModel;

	private Mesh _char;
	private int _charVerts;
	private Skeleton _skeleton = null!;
	private AnimationClip[] _clips = [];
	private Matrix4x4[] _palette = [];
	private Matrix4x4 _charModel;

	private IEngine _engine = null!;
	private Concepts.Camera _cam = null!;
	private Concepts.Camera _sunCam = null!;
	private readonly float[] _skyParams = new float[36];

	// A warm daytime sky whose sun direction matches DemoSun (so the sky's sun glow lines up with the lit shadows).
	private readonly DayNight.State _sky = new(
		DemoSun.Direction,
		new Vector3(1.0f, 0.96f, 0.86f),
		2.0f,
		new Vector3(0.30f, 0.32f, 0.36f),
		new Vector3(0.20f, 0.42f, 0.82f),  // zenith blue
		new Vector3(0.45f, 0.60f, 0.84f)); // horizon (deeper, not washed)

	private ColorHandle _ids = null!;
	private ConstantsHandle _hoverParams = null!;
	private ConstantsHandle _viewproj = null!;
	private ConstantsHandle _sunViewproj = null!;

	private float _bloomThreshold = 1.1f;
	private float _gradeStrength = 0.7f;
	private int _clipIdx;

	public void DefineTechniques(IEngine engine)
	{
		_engine = engine;
		_cam = new Concepts.Camera(engine);
		_sunCam = DemoSun.View(engine);

		engine.DefineTechnique("skin", new TechniqueDescriptor("skin", VertexEntry: "main", FragmentEntry: "main", IsCompute: true, HasFragmentPush: true));
		engine.DefineTechnique("depthonly", new TechniqueDescriptor("depthonly", HasVertexInput: true));
		engine.DefineTechnique("gbuffer", new TechniqueDescriptor("gbuffer", HasVertexInput: true));
		engine.DefineTechnique("ssao", new TechniqueDescriptor("ssao", HasFragmentPush: true));
		engine.DefineTechnique("ssao.blur", new TechniqueDescriptor("ssao_blur"));
		engine.DefineTechnique("lighting", new TechniqueDescriptor("lighting", HasFragmentPush: true));
		engine.DefineTechnique("sky", new TechniqueDescriptor("sky", Blend: BlendMode.AlphaOver));
		// The artifact reuses emissive.slang as an additive forward technique (its own name so it doesn't collide
		// with other demos' "emissive" — the gallery technique registry is last-wins by name).
		engine.DefineTechnique("ruins.emissive", new TechniqueDescriptor("emissive", HasVertexInput: true, Blend: BlendMode.Additive));
		engine.DefineTechnique("id", new TechniqueDescriptor("id", HasVertexInput: true));
		engine.DefineTechnique("outline.seed", new TechniqueDescriptor("outline_seed"));
		engine.DefineTechnique("outline.jfa", new TechniqueDescriptor("outline_jfa", HasFragmentPush: true));
		engine.DefineTechnique("outline.resolve", new TechniqueDescriptor("outline_resolve"));
		engine.DefineTechnique("bloom.prefilter", new TechniqueDescriptor("bloom_prefilter", LinearFilter: true, HasFragmentPush: true));
		engine.DefineTechnique("bloom.down", new TechniqueDescriptor("bloom_sample", LinearFilter: true));
		engine.DefineTechnique("bloom.up", new TechniqueDescriptor("bloom_sample", Blend: BlendMode.Additive, LinearFilter: true));
		engine.DefineTechnique("bloom.composite", new TechniqueDescriptor("bloom_composite", LinearFilter: true, HasFragmentPush: true));
		engine.DefineTechnique("exposure", new TechniqueDescriptor("exposure", VertexEntry: "main", FragmentEntry: "main", IsCompute: true, HasFragmentPush: true));
		engine.DefineTechnique("tonemap", new TechniqueDescriptor("tonemap", HasFragmentPush: true));
		engine.DefineTechnique("grade", new TechniqueDescriptor("grade", HasFragmentPush: true));
		engine.DefineTechnique("blit", new TechniqueDescriptor("blit"));

		var assets = Path.Combine(AppContext.BaseDirectory, "assets");

		// The courtyard: a back gateway (two walls + an arch), a left corner wall, a stacked square tower on the
		// right, scattered rocks, stairs and a tree. Every Kenney piece is a 2×2 footprint with its base at y=-1,
		// so Place() lifts it by its scale to sit on the ground; tiers stack in 2-unit steps.
		AddPiece(engine, assets, "wall", Place(-2f, -4f));
		AddPiece(engine, assets, "tower-square-arch", Place(0f, -4f));
		AddPiece(engine, assets, "wall", Place(2f, -4f));
		AddPiece(engine, assets, "wall-corner", Place(-4f, -4f));
		// A single grounded corner tower base — a ruined stump. (The Kenney mid/top tiers don't stack flush at a
		// 2-unit step, so stacking them reads as floating blocks; a single base sidesteps that and suits "ruins".)
		AddPiece(engine, assets, "tower-square-base", Place(4f, -4f));
		AddPiece(engine, assets, "wall", Place(-4f, -2f, yawDeg: 90f));
		AddPiece(engine, assets, "stairs-stone", Place(1.6f, -1.5f));
		AddPiece(engine, assets, "rocks-large", Place(-2.6f, 1.8f, 0.8f));
		AddPiece(engine, assets, "rocks-small", Place(2.8f, 2.0f, 0.9f));
		AddPiece(engine, assets, "tree-large", Place(-3.8f, 1.5f));

		// The summoning circle (flat ring on the ground) + the floating artifact gem at the centre.
		var (torusV, torusI) = Geometry.BuildTorus(2.4f, 0.18f, 64, 12);
		_torus = engine.CreateMesh(torusV, torusI);
		_torusModel = Matrix4x4.CreateTranslation(0f, 0.05f, 0f);
		var (artV, artI) = Geometry.BuildIcosphere(3);
		_artifact = engine.CreateMesh(artV, artI);
		_artifactModel = Matrix4x4.CreateScale(0.85f) * Matrix4x4.CreateTranslation(0f, 1.9f, 0f);

		// A stone ground plane, sized to the courtyard (not a vast pale plain that dominates the frame).
		const float s = 14f;
		var up = Vector3.UnitY;
		var t = new Vector4(1, 0, 0, 1);
		_ground = engine.CreateMesh(
			[new(new Vector3(-s, 0, -s), up, new Vector2(0, 0), t), new(new Vector3(s, 0, -s), up, new Vector2(1, 0), t),
			 new(new Vector3(s, 0, s), up, new Vector2(1, 1), t), new(new Vector3(-s, 0, s), up, new Vector2(0, 1), t)],
			[0, 1, 2, 0, 2, 3]);

		// The compute-skinned character — the Kenney survivor, converted from FBX (mesh + idle/run/jump clips) to a
		// single GLB by tools/convert_character_fbx.py and loaded through the neutral glTF path.
		var model = GltfLoader.Load(Path.Combine(assets, "kenney", "characters", "character_animated.glb"));
		var prim = model.Primitives[0];
		_char = engine.CreateSkinnedMesh(prim.SkinnedVertices!, prim.Indices);
		_charVerts = prim.SkinnedVertices!.Length;
		_skeleton = model.Skeleton!;
		_clips = [.. model.Clips];
		_palette = new Matrix4x4[_skeleton.JointCount];
		_charModel = Matrix4x4.CreateScale(0.9f) * Matrix4x4.CreateTranslation(-2.6f, 0f, 1.0f);
	}

	private void AddPiece(IEngine engine, string assets, string name, Matrix4x4 model)
	{
		var loaded = GltfLoader.Load(Path.Combine(assets, "kenney", "castle", name + ".glb"));
		foreach (var prim in loaded.Primitives)
			_pieces.Add((engine.CreateMesh(prim.Vertices, prim.Indices), model));
	}

	// Place a 2×2, base-at-y=-1 Kenney piece on the ground at (x,z): scale, optional yaw, lift so the base sits at
	// ground level (tier 0) or stacks 2 units per tier. System.Numerics row-vector order.
	private static Matrix4x4 Place(float x, float z, float scale = 1f, float yawDeg = 0f, int tier = 0) =>
		Matrix4x4.CreateScale(scale)
		* Matrix4x4.CreateRotationY(yawDeg * MathF.PI / 180f)
		* Matrix4x4.CreateTranslation(x, scale + tier * 2f * scale, z);

	public Concepts.Camera? GetCamera(float aspect, float time)
	{
		// Close + low, looking up into the gateway/tower so the stone fills the frame (not a far miniature, and the
		// raised target keeps the ground from eating the lower half).
		var eye = new Vector3(MathF.Sin(time * 0.12f) * 3.5f, 3.0f, 6.5f);
		return _cam.Perspective(MathF.PI / 3f, aspect, 0.3f, 100f).LookAt(eye, new Vector3(0f, 2.2f, -2.5f));
	}

	public void Update(Concepts.Camera camera)
	{
		_cam = camera;
		_engine.Write(_viewproj, camera.ViewProjection);
		_engine.Write(_sunViewproj, _sunCam.ViewProjection);
	}

	public void DrawDebugUI()
	{
		ImGuiNET.ImGui.SliderFloat("Bloom threshold", ref _bloomThreshold, 0.2f, 4f);
		ImGuiNET.ImGui.SliderFloat("Grade strength", ref _gradeStrength, 0f, 1f);
		if (_clips.Length > 1)
			ImGuiNET.ImGui.SliderInt("Animation clip", ref _clipIdx, 0, _clips.Length - 1);
	}

	public RenderGraph Build(DemoScene scene) => RenderGraph.Define(g =>
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
		var skinned = g.Buffer("ruins_skinned", _charVerts * 48);
		var meter = g.Domain("ruins_meter", 1, 1);
		var exposure = meter.Persistent("ruins_exposure", Format.R16F);

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
				var tt = clip.Duration > 0f ? ctx.Time % clip.Duration : 0f;
				AnimationSampler.Evaluate(_skeleton, clip, tt, _palette);
				ctx.DispatchSkin("skin", _char, _palette, skinned, _charVerts);
			});

		g.Pass("shadow")
			.Reads(_sunViewproj)
			.WritesDepth(shadowMap)
			.ReadsVertex(skinned)
			.Draw(ctx =>
			{
				DrawStone(ctx, "depthonly");
				DemoDraw.Skinned(ctx, _char, skinned, "depthonly", _charModel);
			});

		g.Pass("gbuffer")
			.Reads(_viewproj)
			.Writes(albedo, normal, position)
			.WritesDepth(depth)
			.ReadsVertex(skinned)
			.Draw(ctx =>
			{
				DrawStone(ctx, "gbuffer");
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

		// The glowing artifact: additive over the lit scene, depth-tested so the summoning-circle/stone occlude it.
		g.Pass("artifact")
			.Reads(_viewproj)
			.Blends(lit)
			.ReadsDepth(depth)
			.Draw(ctx => DemoDraw.Mesh(ctx, _artifact, "ruins.emissive", _artifactModel));

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

		// Bloom (the artifact glow + outline ring), auto-expose to the lit scene (maps the HDR stone to display),
		// then a desaturated-stone grade.
		var bloomed = g.Bloom(outlined, () => _bloomThreshold, () => 0.55f, mips: 4);

		g.Compute("exposure")
			.Samples(lit)
			.ReadWrites(exposure)
			.Dispatch(ctx => ctx.Dispatch("exposure", (1, 1, 1), [8f * ctx.DeltaTime], [lit], [exposure]));

		g.Pass("tonemap")
			.Samples(bloomed, exposure)
			.Writes(toned)
			.Draw(ctx => ctx.FullScreen("tonemap", [ctx.HdrOutput ? 1f : 0f], bloomed, exposure));

		g.Pass("grade")
			.Samples(toned)
			.Writes(graded)
			.Draw(ctx => ctx.FullScreen("grade", [1.06f, 1.0f, 0.90f, _gradeStrength, 1.18f, 0.92f, 0f, 0f], toned));

		g.Pass("blit").Samples(graded).Writes(screen).Draw(ctx => ctx.FullScreen("blit", graded));
		DemoGraph.Hud(g, screen);
		g.Present(screen);
	});

	// Draw the static stone scene (castle pieces + summoning circle + ground) with the given technique.
	private void DrawStone(IRenderContext ctx, string technique)
	{
		foreach (var (mesh, model) in _pieces)
			DemoDraw.Mesh(ctx, mesh, technique, model);
		DemoDraw.Mesh(ctx, _torus, technique, _torusModel);
		DemoDraw.Mesh(ctx, _ground, technique, Matrix4x4.Identity);
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
