using System.Numerics;
using System.Runtime.InteropServices;

using Loom.Assets;
using Loom.Core.Geometry;
using Loom.Core.Graph;
using Loom.Core.Hosting;
using Loom.Core.Materials;

using SUIsei;
using SUIsei.Controls;

namespace Loom.Sandbox.Demos;

/// <summary>
/// Night glade — a bloom showcase. A cluster of glowing capsule mushrooms (a capsule stem + an emissive dome cap
/// drawn forward at HDR intensity, so it blooms hard) stands in a dense field of long wind-swept grass, ringed by
/// dark Kenney trees, with a dozen warm fireflies wandering through the air. The scene is lit only by a dim cool
/// moon, so the night reads dark while the emissive caps + fireflies blow past the bloom threshold and glow. Pass
/// order: cascade depth → g-buffer (stems + textured trees + ground + grass) → deferred night lighting → sky →
/// emissive caps (additive) → wandering fireflies (additive) → bloom → teal/purple grade → blit. Frozen time pins
/// a deterministic cross-backend golden.
/// </summary>
internal sealed class NightGladeDemo : IDemo
{
	public string Name => "Night Glade";

	private const int CascadeCount = 3;
	private const float ShadowResolution = 1024f;
	private const float CamNear = 0.3f;
	private const float CamFar = 80f;
	private const int Grid = 56;          // 56×56 ≈ 3100 grass blades
	private const float FieldHalf = 11f;  // the field fills the visible ground, not a small patch in a void
	private const int FireflyCount = 14;  // a dozen-odd wandering motes, not a dense fountain

	private readonly record struct Blade(Vector4 PosScale, Vector4 YawPhaseStiffness);

	// Each mushroom is a placed (capsule stem + glowing dome cap); only the position + a uniform scale vary. Scaled
	// large enough that every cap towers clear of the long grass (caps sit at ~0.84·scale, grass tops out ~1.0).
	private readonly (Vector3 Pos, float Scale)[] _mushrooms =
	[
		(new(0f, 0f, 0f), 2.1f),
		(new(2.4f, 0f, 0.9f), 1.5f),
		(new(-2.2f, 0f, 1.5f), 1.7f),
		(new(1.2f, 0f, -2.3f), 1.4f),
		(new(-2.6f, 0f, -1.3f), 1.5f),
		(new(3.0f, 0f, -1.7f), 1.6f),
		(new(-0.7f, 0f, 2.8f), 1.4f),
		(new(4.0f, 0f, 2.0f), 1.45f),
		(new(-4.2f, 0f, -2.8f), 1.55f),
	];

	private Mesh _ground;
	private Mesh _stem;
	private Mesh _cap;
	private Mesh _card;
	private Material _blade;
	private Material _groundMat;  // a dark-earth ground material (the default gbuffer albedo is too bright for night)
	private Material _stemMat;    // a pale material so the mushroom stalks read against the dark night
	private Material _bark;       // the Kenney tree colormap
	private Mesh[] _treeMeshes = [];
	private Matrix4x4[] _treeModels = [];
	private Blade[] _field = [];

	// A hand-authored moonlit night (rather than DayNight's pure midnight, which extinguishes the sun entirely and
	// leaves the scene black save the emissives). A dim cool "moon" directional + a lifted cool ambient keep the
	// stems/grass/trees readable while the emissive caps still bloom.
	private readonly DayNight.State _sun = new(
		Vector3.Normalize(new Vector3(0.35f, 0.62f, -0.40f)), // moon, high in the sky
		new Vector3(0.42f, 0.52f, 0.88f),                     // cool moonlight
		0.35f,                                                // dim — the ground stays dark so the caps pop
		new Vector3(0.05f, 0.06f, 0.12f),                     // low cool ambient (reads grass/tree silhouettes)
		new Vector3(0.02f, 0.03f, 0.10f),                     // deep blue zenith
		new Vector3(0.06f, 0.09f, 0.18f));                    // dark blue horizon (a touch of glow, not pure black)
	private readonly Concepts.Camera[] _cascades = new Concepts.Camera[CascadeCount];
	private readonly float[] _splits = new float[CascadeCount];
	private readonly float[] _csmParams = new float[68];
	private readonly float[] _skyParams = new float[36];
	private readonly float[] _wind = new float[8];
	private IEngine _engine = null!;
	private Concepts.Camera _cam = null!;
	private ConstantsHandle _viewproj = null!;
	private ConstantsHandle _csmSplits = null!;
	private readonly ConstantsHandle[] _cascadeViewproj = new ConstantsHandle[CascadeCount];

	// Live debug knobs (read inside the pass closures, so an ImGui slider takes effect without a graph rebuild).
	private float _bloomThreshold = 1.6f;
	private float _bloomIntensity = 0.22f;
	private float _windStrength = 1.2f;
	private bool _autoOrbit = true;

	public void DefineTechniques(IEngine engine)
	{
		_engine = engine;
		_cam = new Concepts.Camera(engine);
		for (var c = 0; c < CascadeCount; c++)
			_cascades[c] = new Concepts.Camera(engine);

		engine.DefineTechnique("depthonly", new TechniqueDescriptor("depthonly", HasVertexInput: true));
		engine.DefineTechnique("gbuffer", new TechniqueDescriptor("gbuffer", HasVertexInput: true));
		engine.DefineTechnique("gbuffer.nm", new TechniqueDescriptor("gbuffer_normalmap", HasVertexInput: true, LinearFilter: true, Address: SamplerAddress.Repeat));
		engine.DefineTechnique("gbuffer.tex", new TechniqueDescriptor("gbuffer_textured", HasVertexInput: true, LinearFilter: true));
		engine.DefineTechnique("foliage", new TechniqueDescriptor("foliage", HasVertexInput: true, Layout: VertexLayout.MeshStandard, Instance: SandboxVertexLayouts.Foliage));
		engine.DefineTechnique("lighting_csm", new TechniqueDescriptor("lighting_csm"));
		engine.DefineTechnique("fog", new TechniqueDescriptor("fog", HasFragmentPush: true));
		engine.DefineTechnique("sky", new TechniqueDescriptor("sky", Blend: BlendMode.AlphaOver));
		// The glowing dome caps: a self-lit red toadstool dome (additive → blooms), normal-shaded so the dome shape
		// reads and the red separates them from the warm yellow-green fireflies.
		engine.DefineTechnique("glade.cap", new TechniqueDescriptor("glow_cap", HasVertexInput: true, Blend: BlendMode.Additive));
		engine.DefineTechnique("firefly.update", new TechniqueDescriptor("firefly_update", VertexEntry: "main", FragmentEntry: "main", IsCompute: true, HasFragmentPush: true));
		engine.DefineTechnique("firefly.render", new TechniqueDescriptor("firefly_render", Blend: BlendMode.Additive, Instance: SandboxVertexLayouts.ParticlePosition));
		engine.DefineTechnique("bloom.prefilter", new TechniqueDescriptor("bloom_prefilter", LinearFilter: true, HasFragmentPush: true));
		engine.DefineTechnique("bloom.down", new TechniqueDescriptor("bloom_sample", LinearFilter: true));
		engine.DefineTechnique("bloom.up", new TechniqueDescriptor("bloom_sample", Blend: BlendMode.Additive, LinearFilter: true));
		engine.DefineTechnique("bloom.composite", new TechniqueDescriptor("bloom_composite", LinearFilter: true, HasFragmentPush: true));
		engine.DefineTechnique("grade", new TechniqueDescriptor("grade", HasFragmentPush: true));
		engine.DefineTechnique("blit", new TechniqueDescriptor("blit"));

		// A wide, dark ground plane.
		const float s = 16f;
		var up = Vector3.UnitY;
		var t = new Vector4(1, 0, 0, 1);
		_ground = engine.CreateMesh(
			[new(new Vector3(-s, 0, -s), up, new Vector2(0, 0), t), new(new Vector3(s, 0, -s), up, new Vector2(1, 0), t),
			 new(new Vector3(s, 0, s), up, new Vector2(1, 1), t), new(new Vector3(-s, 0, s), up, new Vector2(0, 1), t)],
			[0, 1, 2, 0, 2, 3]);
		_groundMat = engine.CreateMaterial(
			engine.CreateTexture(TextureData.FromRgba8(1, 1, [24, 28, 22, 255], TextureFormat.Rgba8Srgb, generateMips: false)),
			engine.CreateTexture(TextureData.FromRgba8(1, 1, [128, 128, 255, 255], TextureFormat.Rgba8Unorm, generateMips: false)));
		_stemMat = engine.CreateMaterial(
			engine.CreateTexture(TextureData.FromRgba8(1, 1, [216, 210, 194, 255], TextureFormat.Rgba8Srgb, generateMips: false)),
			engine.CreateTexture(TextureData.FromRgba8(1, 1, [128, 128, 255, 255], TextureFormat.Rgba8Unorm, generateMips: false)));

		// One capsule stem + one dome cap, instanced per mushroom by a model matrix (a uniform scale per mushroom).
		var (stemV, stemI) = Geometry.BuildCapsule(0.5f, 0.18f, 16, 8);
		_stem = engine.CreateMesh(stemV, stemI);
		var (capV, capI) = Geometry.BuildHemisphere(10, 28);
		_cap = engine.CreateMesh(capV, capI);

		// Foliage blade card + its cutout texture (a tall, thin, pointed blade — long grass).
		_card = engine.CreateMesh(
			[new(new Vector3(-0.5f, 0f, 0f), up, new Vector2(0, 1), t), new(new Vector3(0.5f, 0f, 0f), up, new Vector2(1, 1), t),
			 new(new Vector3(0.5f, 1f, 0f), up, new Vector2(1, 0), t), new(new Vector3(-0.5f, 1f, 0f), up, new Vector2(0, 0), t)],
			[0, 1, 2, 0, 2, 3]);
		_blade = engine.CreateMaterial(engine.CreateTexture(BuildBladeTexture()));

		var rng = new Random(11);
		var blades = new List<Blade>();
		for (var i = 0; i < Grid * Grid; i++)
		{
			var x = (i % Grid / (float)(Grid - 1) - 0.5f) * 2f * FieldHalf + ((float)rng.NextDouble() - 0.5f) * 0.5f;
			var z = (i / Grid / (float)(Grid - 1) - 0.5f) * 2f * FieldHalf + ((float)rng.NextDouble() - 0.5f) * 0.5f;
			blades.Add(new Blade(
				new Vector4(x, 0f, z, 0.5f + (float)rng.NextDouble() * 0.5f), // 0.5–1.0 tall — long grass under the mushroom caps
				new Vector4((float)rng.NextDouble() * MathF.Tau, (float)rng.NextDouble() * MathF.Tau, 1.4f + (float)rng.NextDouble(), 0f)));
		}
		_field = [.. blades];
	}

	// Load the Kenney "tree-large" piece once, measure its bounds, and place a ring of them around the glade
	// (grounded so the trunk base sits on the floor). Textured from the castle kit's shared colormap. Bytes come
	// through the scene resolver — a file on desktop, an embedded resource in the browser — so the demo loads the
	// same way on both hosts.
	private void LoadTrees(IEngine engine, DemoScene scene)
	{
		var model = GltfLoader.LoadGlb(scene.Asset("kenney/castle/tree-large.glb"));
		var meshes = new List<Mesh>();
		var min = new Vector3(float.MaxValue);
		var max = new Vector3(float.MinValue);
		foreach (var prim in model.Primitives)
		{
			meshes.Add(engine.CreateMesh(prim.Vertices, prim.Indices));
			foreach (var v in prim.Vertices)
			{
				min = Vector3.Min(min, v.Position);
				max = Vector3.Max(max, v.Position);
			}
		}
		_bark = engine.CreateMaterial(engine.CreateTexture(
			ImageLoader.Decode(scene.Asset("kenney/castle/Textures/colormap.png"), TextureFormat.Rgba8Srgb)));

		// A dense ring of trees enclosing the clearing at varied radius/scale, each grounded by the measured base + a
		// yaw spin — a thick wood around the glade, not a sparse fringe.
		const int treeCount = 22;
		var rng = new Random(7);
		var meshesArr = meshes.ToArray();
		var allMeshes = new List<Mesh>();
		var models = new List<Matrix4x4>();
		for (var i = 0; i < treeCount; i++)
		{
			var ang = i / (float)treeCount * MathF.Tau + (float)rng.NextDouble() * 0.5f;
			var radius = 7.5f + (float)rng.NextDouble() * 5.5f;
			var scale = 1.5f + (float)rng.NextDouble() * 1.4f;
			var model2 = Matrix4x4.CreateScale(scale)
				* Matrix4x4.CreateRotationY((float)rng.NextDouble() * MathF.Tau)
				* Matrix4x4.CreateTranslation(MathF.Cos(ang) * radius, -min.Y * scale, MathF.Sin(ang) * radius);
			foreach (var m in meshesArr)
			{
				allMeshes.Add(m);
				models.Add(model2);
			}
		}
		_treeMeshes = [.. allMeshes];
		_treeModels = [.. models];
	}

	public Concepts.Camera? GetCamera(float aspect, float time)
	{
		var angle = _autoOrbit ? time * 0.22f : 0.9f;
		var eye = new Vector3(MathF.Sin(angle) * 7f, 2.6f, MathF.Cos(angle) * 7f);
		return _cam.Perspective(MathF.PI / 3f, aspect, CamNear, CamFar).LookAt(eye, new Vector3(0f, 0.8f, 0f));
	}

	public void Update(Concepts.Camera camera)
	{
		_cam = camera;
		Cascades.Compute(_cam, _sun.SunDir, CamNear, CamFar, ShadowResolution, _cascades, _splits);
		_engine.Write(_csmSplits, new DemoGraph.CsmSplits(new Vector4(_splits[0], _splits[1], _splits[2], 0f)));
		_engine.Write(_viewproj, _cam.ViewProjection);
		for (var c = 0; c < CascadeCount; c++)
			_engine.Write(_cascadeViewproj[c], _cascades[c].ViewProjection);
	}

	private UIElement? _debugPanel;

	public UIElement? DebugPanel => _debugPanel ??= BuildDebugPanel();

	private UIElement BuildDebugPanel()
	{
		var autoOrbit = new CheckBox { Content = "Auto-orbit", IsChecked = _autoOrbit, Margin = new Thickness(0f, 0f, 0f, 6f) };
		autoOrbit.Checked += (_, _) => _autoOrbit = true;
		autoOrbit.Unchecked += (_, _) => _autoOrbit = false;
		var panel = new StackPanel();
		panel.Children.Add(autoOrbit);
		panel.Children.Add(DebugSlider.Labeled("Bloom threshold", _bloomThreshold, 0.2f, 5f, v => _bloomThreshold = v));
		panel.Children.Add(DebugSlider.Labeled("Bloom intensity", _bloomIntensity, 0.1f, 2f, v => _bloomIntensity = v));
		panel.Children.Add(DebugSlider.Labeled("Wind", _windStrength, 0f, 3f, v => _windStrength = v));
		return panel;
	}

	public RenderGraph Build(DemoScene scene)
	{
		// A ring of dark Kenney trees enclosing the glade — silhouettes against the moonlit sky, so the field reads
		// as a clearing in a wood rather than a patch in a void. Textured from the kit's colormap, grounded by AABB.
		LoadTrees(_engine, scene);

		return RenderGraph.Define(g =>
	{
		var screen = g.Backbuffer;
		var albedo = g.Screen.Color("ng_albedo", Format.Rgba8Srgb);
		var normal = g.Screen.Color("ng_normal", Format.Rgba16F);
		var position = g.Screen.Color("ng_position", Format.Rgba16F);
		var depth = g.Screen.Depth("ng_depth", Format.Depth32F);
		var lit = g.Screen.Color("ng_lit", Format.Rgba16F);
		var litFog = g.Screen.Color("ng_litfog", Format.Rgba16F);
		var graded = g.Screen.Color("ng_graded", Format.Rgba16F);
		var fireflies = g.Buffer("ng_fireflies", FireflyCount * 16); // float4 per firefly
		_viewproj = g.Constants<Matrix4x4>("nightglade.viewproj");
		_csmSplits = g.Constants<DemoGraph.CsmSplits>("nightglade.csm.splits");

		var cascadeMaps = new DepthHandle[CascadeCount];
		for (var c = 0; c < CascadeCount; c++)
		{
			cascadeMaps[c] = g.Domain($"ng_csm{c}", (int)ShadowResolution, (int)ShadowResolution).Depth($"ng_csm_depth{c}", Format.Depth32F);
			_cascadeViewproj[c] = g.Constants<Matrix4x4>($"nightglade.cascade{c}.viewproj");
		}

		for (var c = 0; c < CascadeCount; c++)
		{
			var cascade = c;
			g.Pass($"shadow{c}")
				.Reads(_cascadeViewproj[cascade])
				.WritesDepth(cascadeMaps[cascade])
				.Draw(ctx =>
				{
					foreach (var m in _mushrooms)
						DemoDraw.Mesh(ctx, _stem, "depthonly", StemModel(m.Pos, m.Scale));
					for (var i = 0; i < _treeMeshes.Length; i++)
						DemoDraw.Mesh(ctx, _treeMeshes[i], "depthonly", _treeModels[i]);
					DemoDraw.Mesh(ctx, _ground, "depthonly", Matrix4x4.Identity);
				});
		}

		g.Pass("gbuffer")
			.Reads(_viewproj)
			.Writes(albedo, normal, position)
			.WritesDepth(depth)
			.Draw(ctx =>
			{
				foreach (var m in _mushrooms)
					DemoDraw.Mesh(ctx, _stem, "gbuffer.nm", StemModel(m.Pos, m.Scale), 0, _stemMat);
				for (var i = 0; i < _treeMeshes.Length; i++)
					DemoDraw.Mesh(ctx, _treeMeshes[i], "gbuffer.tex", _treeModels[i], 0, _bark);
				DemoDraw.Mesh(ctx, _ground, "gbuffer.nm", Matrix4x4.Identity, 0, _groundMat); // dark-earth ground

				_wind[0] = 1f; _wind[1] = 0.35f; _wind[2] = ctx.Time; _wind[3] = 0f;
				_wind[4] = 0.14f * _windStrength; _wind[5] = 2.0f; _wind[6] = 0f; _wind[7] = 0f;
				var foliage = new DrawCall { Mesh = _card, Material = _blade, Technique = "foliage", Instances = DrawCall.From<Blade>(_field) };
				foliage.SetUniforms<float>(_wind);
				ctx.Draw(foliage);
			});

		g.Pass("lighting")
			.Samples(albedo, normal, position, cascadeMaps[0], cascadeMaps[1], cascadeMaps[2])
			.Reads(_csmSplits)
			.Writes(lit)
			.Draw(ctx => ctx.FullScreen("lighting_csm", PackLighting(), albedo, normal, position, cascadeMaps[0], cascadeMaps[1], cascadeMaps[2]));

		// Distance fog: fade the lit ground/trees into a dark night haze so the ground plane recedes instead of
		// ending at a hard quad edge. (Background texels pass through; the sky pass fills them.)
		g.Pass("fog")
			.Samples(lit, position)
			.Writes(litFog)
			.Draw(ctx =>
			{
				Matrix4x4.Invert(_cam.View, out var iv);
				var e = iv.Translation;
				ctx.FullScreen("fog", [e.X, e.Y, e.Z, 5f, 0.03f, 0.05f, 0.12f, 1f / 16f], lit, position);
			});

		g.Pass("sky").Blends(litFog).Samples(depth).Draw(ctx => ctx.FullScreen("sky", PackSky(), depth));

		// Emissive dome caps: additive over the fogged scene, depth-tested against the opaque stems/ground so a cap
		// behind a nearer mushroom is occluded. Their HDR emission (>> 1) feeds the bloom hard.
		g.Pass("caps")
			.Reads(_viewproj)
			.Blends(litFog)
			.ReadsDepth(depth)
			.Draw(ctx =>
			{
				foreach (var m in _mushrooms)
					DemoDraw.Mesh(ctx, _cap, "glade.cap", CapModel(m.Pos, m.Scale));
			});

		// Wandering fireflies: a compute pass writes one float4 (position + brightness) per mote; the render pass
		// reads it as the per-instance stream and additively blends warm glowing billboards drifting through the air.
		g.Compute("firefly.update")
			.ReadWrites(fireflies)
			.Dispatch(ctx => ctx.Dispatch("firefly.update", (1, 1, 1), [ctx.Time], [], [fireflies]));

		g.Pass("fireflies")
			.Reads(_viewproj)
			.Blends(litFog)
			.ReadsVertex(fireflies)
			.Draw(ctx => ctx.Draw(new DrawCall { Technique = "firefly.render", VertexCount = 6, Instances = DrawCall.Gpu(fireflies, FireflyCount) }));

		// Bloom the HDR fogged result (live threshold/intensity). No auto-exposure: on a deliberately dark night it
		// would open up and brighten the darks back to "daytime"; the bright emissive caps + fireflies instead clip
		// to a bloomed glow, which is the night look we want.
		var bloomed = g.Bloom(litFog, () => _bloomThreshold, () => _bloomIntensity, mips: 4);

		// A cool teal/purple night grade.
		g.Pass("grade")
			.Samples(bloomed)
			.Writes(graded)
			.Draw(ctx => ctx.FullScreen("grade", [0.72f, 0.86f, 1.0f, 0.45f, 1.12f, 1.2f, 0f, 0f], bloomed));

		g.Pass("blit").Samples(graded).Writes(screen).Draw(ctx => ctx.FullScreen("blit", graded));
		DemoGraph.Hud(g, screen, scene);
		g.Present(screen);
	});
	}

	// A mushroom stem: a thick capsule stalk translated so its rounded base sits on the ground (the unit capsule
	// spans ±0.43 in Y → lift by 0.43·scale), widened in X/Z so the pale stalk reads clearly. Row-vector order.
	private static Matrix4x4 StemModel(Vector3 pos, float scale) =>
		Matrix4x4.CreateScale(1.25f * scale, scale, 1.25f * scale) * Matrix4x4.CreateTranslation(pos.X, 0.43f * scale, pos.Z);

	// A mushroom cap: a hemisphere dome (radius ≈ twice the stem's) whose crown sits just above the stem top, the
	// flat underside overhanging the stalk. The unit dome is radius 0.5 (top at y=0.5), so scale 0.9·s gives a cap
	// radius 0.45·s (the stem renders at ≈0.225·s) and a crown 0.45·s above the disc; lifting the disc to 0.50·s
	// puts the crown at 0.95·s — a touch over the ≈0.86·s stem top.
	private static Matrix4x4 CapModel(Vector3 pos, float scale) =>
		Matrix4x4.CreateScale(0.9f * scale) * Matrix4x4.CreateTranslation(pos.X, 0.50f * scale, pos.Z);

	// The deferred CSM lighting block: 3 cascade view-projections, camera eye/forward, and the night sun/ambient.
	private ReadOnlySpan<float> PackLighting()
	{
		var p = _csmParams;
		for (var c = 0; c < CascadeCount; c++)
		{
			var m = _cascades[c].ViewProjection;
			MemoryMarshal.CreateReadOnlySpan(in m.M11, 16).CopyTo(p.AsSpan(c * 16, 16));
		}
		Matrix4x4.Invert(_cam.View, out var invView);
		var eye = invView.Translation;
		var forward = -Vector3.Normalize(new Vector3(invView.M31, invView.M32, invView.M33));
		p[48] = eye.X; p[49] = eye.Y; p[50] = eye.Z; p[51] = 0f;
		p[52] = forward.X; p[53] = forward.Y; p[54] = forward.Z; p[55] = 0f;
		p[56] = _sun.SunDir.X; p[57] = _sun.SunDir.Y; p[58] = _sun.SunDir.Z; p[59] = _sun.Intensity;
		p[60] = _sun.SunColor.X; p[61] = _sun.SunColor.Y; p[62] = _sun.SunColor.Z; p[63] = 0f;
		p[64] = _sun.Ambient.X; p[65] = _sun.Ambient.Y; p[66] = _sun.Ambient.Z; p[67] = 0f;
		return p;
	}

	private ReadOnlySpan<float> PackSky()
	{
		var p = _skyParams;
		Matrix4x4.Invert(_cam.ViewProjection, out var invVp);
		MemoryMarshal.CreateReadOnlySpan(in invVp.M11, 16).CopyTo(p.AsSpan(0, 16));
		Matrix4x4.Invert(_cam.View, out var invView);
		var eye = invView.Translation;
		p[16] = eye.X; p[17] = eye.Y; p[18] = eye.Z; p[19] = 0f;
		p[20] = _sun.SunDir.X; p[21] = _sun.SunDir.Y; p[22] = _sun.SunDir.Z; p[23] = 0f;
		p[24] = _sun.Zenith.X; p[25] = _sun.Zenith.Y; p[26] = _sun.Zenith.Z; p[27] = 0f;
		p[28] = _sun.Horizon.X; p[29] = _sun.Horizon.Y; p[30] = _sun.Horizon.Z; p[31] = 0f;
		p[32] = _sun.SunColor.X; p[33] = _sun.SunColor.Y; p[34] = _sun.SunColor.Z; p[35] = 220f;
		return p;
	}

	// A tall, thin, pointed grass blade with an alpha cutout: widest at the base, tapering to a point at the tip
	// (the natural blade silhouette), on a vertical green gradient. The previous fan-shaped cutout read as a flat
	// patch; this reads as long grass.
	private static TextureData BuildBladeTexture()
	{
		const int w = 16, h = 64;
		var rgba = new byte[w * h * 4];
		for (var py = 0; py < h; py++)
		{
			var v = py / (float)(h - 1);          // 0 at the image top (tip), 1 at the bottom (base)
			var fromBase = 1f - v;                // 0 at base, 1 at tip
			var halfWidth = float.Lerp(0.20f, 0.0f, fromBase); // wide base → pointed tip
			var shade = float.Lerp(0.30f, 0.62f, fromBase);    // darker base, brighter tip
			for (var px = 0; px < w; px++)
			{
				var o = (py * w + px) * 4;
				rgba[o + 0] = (byte)(0.16f * shade * 255);
				rgba[o + 1] = (byte)(shade * 255);
				rgba[o + 2] = (byte)(0.18f * shade * 255);
				rgba[o + 3] = MathF.Abs(px / (float)(w - 1) - 0.5f) < halfWidth ? (byte)255 : (byte)0;
			}
		}
		return TextureData.FromRgba8(w, h, rgba, TextureFormat.Rgba8Srgb, generateMips: false);
	}
}
