using System.Numerics;
using System.Runtime.InteropServices;

using Loom.Core.Geometry;
using Loom.Core.Graph;
using Loom.Core.Hosting;
using Loom.Core.Materials;

namespace Loom.Sandbox.Demos;

/// <summary>
/// Night glade — a bloom + auto-exposure showcase. A cluster of glowing capsule mushrooms (a capsule stem + an
/// emissive dome cap drawn forward at HDR intensity, so it blooms hard) stands in a windy grass field under a deep
/// blue night sky, with a slow fountain of warm spark "fireflies" rising through the centre. The scene is lit only
/// by a dim night ambient, so the auto-exposure opens up to reveal the foliage while the emissive caps + sparks
/// blow past the bloom threshold. Pass order: cascade depth → g-buffer (stems + ground + foliage) → deferred night
/// lighting → sky → emissive caps (additive) → spark fireflies (additive) → bloom → auto-exposure → tonemap →
/// teal/purple grade → blit. Frozen time pins a deterministic cross-backend golden.
/// </summary>
internal sealed class NightGladeDemo : IDemo
{
	public string Name => "Night Glade";

	private const int CascadeCount = 3;
	private const float ShadowResolution = 1024f;
	private const float CamNear = 0.3f;
	private const float CamFar = 60f;
	private const int Grid = 24;
	private const float FieldHalf = 4f;
	private const int ParticleCount = 64; // one workgroup; kept sparse so the rising sparks read as glowing spores from the hero mushroom, not a dense cone

	private readonly record struct Blade(Vector4 PosScale, Vector4 YawPhaseStiffness);

	// Each mushroom is a placed (capsule stem + emissive dome cap); only the position + a uniform scale vary.
	private readonly (Vector3 Pos, float Scale)[] _mushrooms =
	[
		(new(0f, 0f, 0f), 1.3f),
		(new(1.6f, 0f, 0.6f), 0.8f),
		(new(-1.4f, 0f, 1.0f), 1.0f),
		(new(0.8f, 0f, -1.5f), 0.7f),
		(new(-1.6f, 0f, -0.8f), 0.9f),
		(new(2.0f, 0f, -1.0f), 1.1f),
		(new(-0.4f, 0f, 1.8f), 0.75f),
	];

	private Mesh _ground;
	private Mesh _stem;
	private Mesh _cap;
	private Mesh _card;
	private Material _blade;
	private Material _groundMat; // a dark-earth ground material (the default gbuffer albedo is too bright for night)
	private Blade[] _field = [];

	// A hand-authored moonlit night (rather than DayNight's pure midnight, which extinguishes the sun entirely and
	// leaves the scene black save the emissives — the auto-exposure then over-opens and blows them out). A dim cool
	// "moon" directional + a lifted cool ambient keep the stems/grass readable while the emissive caps still bloom.
	private readonly DayNight.State _sun = new(
		Vector3.Normalize(new Vector3(0.35f, 0.65f, -0.40f)), // moon, high in the sky
		new Vector3(0.40f, 0.50f, 0.85f),                     // cool moonlight
		0.3f,                                                 // dim — the ground should stay dark so the emissive caps pop
		new Vector3(0.04f, 0.05f, 0.10f),                     // low cool ambient (just enough to read grass silhouettes)
		new Vector3(0.02f, 0.03f, 0.10f),                     // deep blue zenith
		new Vector3(0.04f, 0.06f, 0.14f));                    // dark blue horizon
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
	private float _bloomThreshold = 2.0f;  // only the emissive caps/sparks (emission >> 1) bloom — a tight glow, not a frame-wide wash
	private float _bloomIntensity = 0.18f;
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
		engine.DefineTechnique("foliage", new TechniqueDescriptor("foliage", HasVertexInput: true, Layout: VertexLayout.MeshStandard, Instance: SandboxVertexLayouts.Foliage));
		engine.DefineTechnique("lighting_csm", new TechniqueDescriptor("lighting_csm"));
		engine.DefineTechnique("sky", new TechniqueDescriptor("sky", Blend: BlendMode.AlphaOver));
		// The emissive dome caps reuse emissive.slang but as an ADDITIVE forward technique (its own name so it doesn't
		// clobber the opaque "emissive" the bloom demo registers — the gallery's technique registry is last-wins by name).
		engine.DefineTechnique("glade.cap", new TechniqueDescriptor("emissive", HasVertexInput: true, Blend: BlendMode.Additive));
		engine.DefineTechnique("particle.update", new TechniqueDescriptor("particle_update", VertexEntry: "main", FragmentEntry: "main", IsCompute: true, HasFragmentPush: true));
		engine.DefineTechnique("particle.render", new TechniqueDescriptor("particle_render", Blend: BlendMode.Additive, Instance: SandboxVertexLayouts.ParticlePosition));
		engine.DefineTechnique("bloom.prefilter", new TechniqueDescriptor("bloom_prefilter", LinearFilter: true, HasFragmentPush: true));
		engine.DefineTechnique("bloom.down", new TechniqueDescriptor("bloom_sample", LinearFilter: true));
		engine.DefineTechnique("bloom.up", new TechniqueDescriptor("bloom_sample", Blend: BlendMode.Additive, LinearFilter: true));
		engine.DefineTechnique("bloom.composite", new TechniqueDescriptor("bloom_composite", LinearFilter: true, HasFragmentPush: true));
		engine.DefineTechnique("grade", new TechniqueDescriptor("grade", HasFragmentPush: true));
		engine.DefineTechnique("blit", new TechniqueDescriptor("blit"));

		// A wide, dark ground plane.
		const float s = 15f;
		var up = Vector3.UnitY;
		var t = new Vector4(1, 0, 0, 1);
		_ground = engine.CreateMesh(
			[new(new Vector3(-s, 0, -s), up, new Vector2(0, 0), t), new(new Vector3(s, 0, -s), up, new Vector2(1, 0), t),
			 new(new Vector3(s, 0, s), up, new Vector2(1, 1), t), new(new Vector3(-s, 0, s), up, new Vector2(0, 1), t)],
			[0, 1, 2, 0, 2, 3]);
		// A dark-earth ground material (albedo + flat normal) so the floor stays dark at night — the stock gbuffer's
		// hardcoded warm albedo reads as a pale sheet under any lighting.
		_groundMat = engine.CreateMaterial(
			engine.CreateTexture(TextureData.FromRgba8(1, 1, [26, 30, 24, 255], TextureFormat.Rgba8Srgb, generateMips: false)),
			engine.CreateTexture(TextureData.FromRgba8(1, 1, [128, 128, 255, 255], TextureFormat.Rgba8Unorm, generateMips: false)));

		// One capsule stem + one dome cap, instanced per mushroom by a model matrix (a uniform scale per mushroom).
		var (stemV, stemI) = Geometry.BuildCapsule(0.5f, 0.18f, 16, 8);
		_stem = engine.CreateMesh(stemV, stemI);
		var (capV, capI) = Geometry.BuildSphere(12, 24);
		_cap = engine.CreateMesh(capV, capI);

		// Foliage blade card + its cutout texture (a small night-grass field).
		_card = engine.CreateMesh(
			[new(new Vector3(-0.5f, 0f, 0f), up, new Vector2(0, 1), t), new(new Vector3(0.5f, 0f, 0f), up, new Vector2(1, 1), t),
			 new(new Vector3(0.5f, 1f, 0f), up, new Vector2(1, 0), t), new(new Vector3(-0.5f, 1f, 0f), up, new Vector2(0, 0), t)],
			[0, 1, 2, 0, 2, 3]);
		_blade = engine.CreateMaterial(engine.CreateTexture(BuildBladeTexture()));

		var rng = new Random(11);
		var blades = new List<Blade>();
		for (var i = 0; i < Grid * Grid; i++)
		{
			var x = (i % Grid / (float)(Grid - 1) - 0.5f) * 2f * FieldHalf + ((float)rng.NextDouble() - 0.5f) * 0.4f;
			var z = (i / Grid / (float)(Grid - 1) - 0.5f) * 2f * FieldHalf + ((float)rng.NextDouble() - 0.5f) * 0.4f;
			blades.Add(new Blade(
				new Vector4(x, 0f, z, 0.3f + (float)rng.NextDouble() * 0.4f),
				new Vector4((float)rng.NextDouble() * MathF.Tau, (float)rng.NextDouble() * MathF.Tau, 1.4f + (float)rng.NextDouble(), 0f)));
		}
		_field = [.. blades];
	}

	public Concepts.Camera? GetCamera(float aspect, float time)
	{
		var angle = _autoOrbit ? time * 0.25f : 0.9f;
		var eye = new Vector3(MathF.Sin(angle) * 6f, 2.5f, MathF.Cos(angle) * 6f);
		return _cam.Perspective(MathF.PI / 3f, aspect, CamNear, CamFar).LookAt(eye, new Vector3(0f, 0.7f, 0f));
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

	public void DrawDebugUI()
	{
		ImGuiNET.ImGui.SliderFloat("Bloom threshold", ref _bloomThreshold, 0.2f, 5f);
		ImGuiNET.ImGui.SliderFloat("Bloom intensity", ref _bloomIntensity, 0.1f, 2f);
		ImGuiNET.ImGui.SliderFloat("Wind", ref _windStrength, 0f, 3f);
		ImGuiNET.ImGui.Checkbox("Auto-orbit", ref _autoOrbit);
	}

	public RenderGraph Build(DemoScene scene) => RenderGraph.Define(g =>
	{
		var screen = g.Backbuffer;
		var albedo = g.Screen.Color("ng_albedo", Format.Rgba8Srgb);
		var normal = g.Screen.Color("ng_normal", Format.Rgba16F);
		var position = g.Screen.Color("ng_position", Format.Rgba16F);
		var depth = g.Screen.Depth("ng_depth", Format.Depth32F);
		var lit = g.Screen.Color("ng_lit", Format.Rgba16F);
		var graded = g.Screen.Color("ng_graded", Format.Rgba16F);
		var particles = g.Buffer("ng_particles", ParticleCount * 16); // float4 per particle
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
					DemoDraw.Mesh(ctx, _stem, "gbuffer", StemModel(m.Pos, m.Scale));
				DemoDraw.Mesh(ctx, _ground, "gbuffer.nm", Matrix4x4.Identity, 0, _groundMat); // dark-earth ground

				_wind[0] = 1f; _wind[1] = 0.35f; _wind[2] = ctx.Time; _wind[3] = 0f;
				_wind[4] = 0.12f * _windStrength; _wind[5] = 2.0f; _wind[6] = 0f; _wind[7] = 0f;
				var foliage = new DrawCall { Mesh = _card, Material = _blade, Technique = "foliage", Instances = DrawCall.From<Blade>(_field) };
				foliage.SetUniforms<float>(_wind);
				ctx.Draw(foliage);
			});

		g.Pass("lighting")
			.Samples(albedo, normal, position, cascadeMaps[0], cascadeMaps[1], cascadeMaps[2])
			.Reads(_csmSplits)
			.Writes(lit)
			.Draw(ctx => ctx.FullScreen("lighting_csm", PackLighting(), albedo, normal, position, cascadeMaps[0], cascadeMaps[1], cascadeMaps[2]));

		g.Pass("sky").Blends(lit).Samples(depth).Draw(ctx => ctx.FullScreen("sky", PackSky(), depth));

		// Emissive dome caps: additive over the lit scene, depth-tested against the opaque stems/ground so a cap
		// behind a nearer mushroom is occluded. Their HDR emission (>> 1) feeds the bloom hard.
		g.Pass("caps")
			.Reads(_viewproj)
			.Blends(lit)
			.ReadsDepth(depth)
			.Draw(ctx =>
			{
				foreach (var m in _mushrooms)
					DemoDraw.Mesh(ctx, _cap, "glade.cap", CapModel(m.Pos, m.Scale));
			});

		// Spark fireflies: a compute fountain writes the particle buffer; the render pass reads it as the per-instance
		// stream and additively blends small clip-space-sized warm sparks (HDR → they bloom) rising through the glade.
		g.Compute("particle.update")
			.ReadWrites(particles)
			.Dispatch(ctx => ctx.Dispatch("particle.update", ((uint)(ParticleCount / 64), 1, 1), [ctx.Time], [], [particles]));

		g.Pass("fireflies")
			.Reads(_viewproj)
			.Blends(lit)
			.ReadsVertex(particles)
			.Draw(ctx => ctx.Draw(new DrawCall { Technique = "particle.render", VertexCount = 6, Instances = DrawCall.Gpu(particles, ParticleCount) }));

		// Bloom the HDR lit result (live threshold/intensity). No auto-exposure: on a deliberately dark night it
		// would open up and brighten the darks back to "daytime", washing out the mood — the bright emissive caps +
		// sparks instead clip to a bloomed glow, which is the night look we want.
		var bloomed = g.Bloom(lit, () => _bloomThreshold, () => _bloomIntensity, mips: 3);

		// A cool teal/purple night grade.
		g.Pass("grade")
			.Samples(bloomed)
			.Writes(graded)
			.Draw(ctx => ctx.FullScreen("grade", [0.72f, 0.86f, 1.0f, 0.45f, 1.12f, 1.2f, 0f, 0f], bloomed));

		g.Pass("blit").Samples(graded).Writes(screen).Draw(ctx => ctx.FullScreen("blit", graded));
		DemoGraph.Hud(g, screen);
		g.Present(screen);
	});

	// A mushroom stem: a uniform-scaled capsule translated so its rounded base sits on the ground (the unit capsule
	// spans ±0.43 in Y → lift by 0.43·scale). System.Numerics row-vector order (scale then translate).
	private static Matrix4x4 StemModel(Vector3 pos, float scale) =>
		Matrix4x4.CreateScale(scale) * Matrix4x4.CreateTranslation(pos.X, 0.43f * scale, pos.Z);

	// A mushroom cap: a flattened dome (the unit sphere squashed in Y) overhanging the stem, perched at its top.
	private static Matrix4x4 CapModel(Vector3 pos, float scale) =>
		Matrix4x4.CreateScale(0.5f * scale, 0.32f * scale, 0.5f * scale) * Matrix4x4.CreateTranslation(pos.X, 0.84f * scale, pos.Z);

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

	// A vertical green blade gradient with an alpha cutout — the same foliage cutout the stylized field uses.
	private static TextureData BuildBladeTexture()
	{
		const int w = 24, h = 48;
		var rgba = new byte[w * h * 4];
		for (var py = 0; py < h; py++)
		{
			var v = py / (float)(h - 1);
			var halfWidth = float.Lerp(0.06f, 0.42f, v);
			var shade = float.Lerp(0.6f, 0.26f, v);
			for (var px = 0; px < w; px++)
			{
				var o = (py * w + px) * 4;
				rgba[o + 0] = (byte)(0.18f * shade * 255);
				rgba[o + 1] = (byte)(shade * 255);
				rgba[o + 2] = (byte)(0.20f * shade * 255);
				rgba[o + 3] = MathF.Abs(px / (float)(w - 1) - 0.5f) < halfWidth ? (byte)255 : (byte)0;
			}
		}
		return TextureData.FromRgba8(w, h, rgba, TextureFormat.Rgba8Srgb, generateMips: false);
	}
}
