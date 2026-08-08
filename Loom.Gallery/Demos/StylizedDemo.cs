using System.Numerics;
using System.Runtime.InteropServices;

using Loom.Assets;
using Loom.Core.Animation;
using Loom.Core.Geometry;
using Loom.Core.Graph;
using Loom.Core.Hosting;
using Loom.Core.Materials;

namespace Loom.Gallery.Demos;

/// <summary>
/// Stylized 3D reference frame — composes every capability end to end: a compute-pre-skinned animated glTF
/// character in a windy alpha-tested foliage field, under cascaded sun shadows and a day/night sky, shaded with
/// cel/toon deferred lighting and a warm season grade. Pass order: skin compute → N cascade depth (char +
/// ground) → g-buffer (char + ground + foliage) → sky → toon lighting → grade → blit. The skinned buffer is
/// produced once and read by every depth + g-buffer pass — the "skin once, read many" payoff the render graph
/// derives. Frozen time pins a deterministic cross-backend golden.
/// </summary>
internal sealed class StylizedDemo : IDemo
{
	public string Name => "Stylized reference";

	private const int CascadeCount = 3;
	private const float ShadowResolution = 1024f;
	private const float CamNear = 0.5f;
	private const float CamFar = 60f;
	private const int Grid = 40;
	private const float FieldHalf = 11f;
	private const float Clearing = 2.8f; // keep blades clear of the character

	private readonly record struct Blade(Vector4 PosScale, Vector4 YawPhaseStiffness);

	private Mesh _ground;
	private Mesh _card;
	private Material _blade;
	private Mesh _char;
	private int _charVerts;
	private Skeleton _skeleton = null!;
	private AnimationClip _clip = null!;
	private Matrix4x4[] _palette = [];
	private Blade[] _field = [];

	private DayNight.State _sun = DayNight.Compute(0.34f);
	private readonly Concepts.Camera[] _cascades = new Concepts.Camera[CascadeCount];
	private readonly float[] _splits = new float[CascadeCount];
	private readonly float[] _csmParams = new float[68];
	private readonly float[] _skyParams = new float[36];
	private readonly float[] _wind = new float[8];
	private IEngine _engine = null!;
	private Concepts.Camera _cam = null!;
	private ConstantsHandle _csmSplits = null!;
	private ConstantsHandle _viewproj = null!;
	private readonly ConstantsHandle[] _cascadeViewproj = new ConstantsHandle[CascadeCount];

	public void DefineTechniques(IEngine engine)
	{
		_engine = engine;
		_cam = new Concepts.Camera(engine);
		for (var c = 0; c < CascadeCount; c++)
			_cascades[c] = new Concepts.Camera(engine);
		engine.DefineTechnique("skin", TechniqueDescriptor.Compute("skin", hasFragmentPush: true));
		engine.DefineTechnique("depthonly", new TechniqueDescriptor("depthonly", HasVertexInput: true));
		engine.DefineTechnique("gbuffer", new TechniqueDescriptor("gbuffer", HasVertexInput: true));
		engine.DefineTechnique("foliage", new TechniqueDescriptor("foliage", HasVertexInput: true, Layout: VertexLayout.MeshStandard, Instance: SandboxVertexLayouts.Foliage));
		engine.DefineTechnique("lighting_toon", new TechniqueDescriptor("lighting_toon"));
		engine.DefineTechnique("sky", new TechniqueDescriptor("sky", Blend: BlendMode.AlphaOver));
		engine.DefineTechnique("grade", new TechniqueDescriptor("grade", HasFragmentPush: true));
		engine.DefineTechnique("blit", new TechniqueDescriptor("blit"));

		// Ground.
		const float s = 40f, y = 0f;
		var up = Vector3.UnitY;
		var t = new Vector4(1, 0, 0, 1);
		_ground = engine.CreateMesh(
			[new(new Vector3(-s, y, -s), up, new Vector2(0, 0), t), new(new Vector3(s, y, -s), up, new Vector2(1, 0), t),
			 new(new Vector3(s, y, s), up, new Vector2(1, 1), t), new(new Vector3(-s, y, s), up, new Vector2(0, 1), t)],
			[0, 1, 2, 0, 2, 3]);

		// Blade card + texture (the foliage cutout).
		_card = engine.CreateMesh(
			[new(new Vector3(-0.5f, 0f, 0f), up, new Vector2(0, 1), t), new(new Vector3(0.5f, 0f, 0f), up, new Vector2(1, 1), t),
			 new(new Vector3(0.5f, 1f, 0f), up, new Vector2(1, 0), t), new(new Vector3(-0.5f, 1f, 0f), up, new Vector2(0, 0), t)],
			[0, 1, 2, 0, 2, 3]);
		_blade = engine.CreateMaterial(engine.CreateTexture(BuildBladeTexture()));

		// Foliage field with a clearing around the character.
		var rng = new Random(7);
		var blades = new List<Blade>();
		for (var i = 0; i < Grid * Grid; i++)
		{
			var x = (i % Grid / (float)(Grid - 1) - 0.5f) * 2f * FieldHalf + ((float)rng.NextDouble() - 0.5f) * 0.6f;
			var z = (i / Grid / (float)(Grid - 1) - 0.5f) * 2f * FieldHalf + ((float)rng.NextDouble() - 0.5f) * 0.6f;
			if (x * x + z * z < Clearing * Clearing)
				continue;
			blades.Add(new Blade(
				new Vector4(x, 0f, z, 0.8f + (float)rng.NextDouble() * 0.7f),
				new Vector4((float)rng.NextDouble() * MathF.Tau, (float)rng.NextDouble() * MathF.Tau, 1.4f + (float)rng.NextDouble(), 0f)));
		}
		_field = [.. blades];
	}

	public Concepts.Camera? GetCamera(float aspect, float time) => _cam
		.Perspective(MathF.PI / 3f, aspect, CamNear, CamFar)
		.LookAt(new Vector3(4.5f, 3.2f, 8.5f), new Vector3(0f, 1.1f, 0f));

	// Fit the cascades to the camera + sun and publish each cascade's view-projection (the depth passes), the
	// camera view-projection (the g-buffer), and the splits (the lighting pass).
	public void Update(Concepts.Camera camera)
	{
		_cam = camera;
		Cascades.Compute(_cam, _sun.SunDir, CamNear, CamFar, ShadowResolution, _cascades, _splits);
		_engine.Write(_csmSplits, new DemoGraph.CsmSplits(new Vector4(_splits[0], _splits[1], _splits[2], 0f)));
		_engine.Write(_viewproj, _cam.ViewProjection);
		for (var c = 0; c < CascadeCount; c++)
			_engine.Write(_cascadeViewproj[c], _cascades[c].ViewProjection);
	}

	// The rigged character (compute-skinned), scaled up a touch and standing on the ground. Bytes come through the
	// scene resolver — a file read on desktop, an HTTP fetch in the browser — so the demo loads the same way, and
	// just as non-blockingly, on both hosts.
	public async Task PreloadAsync(DemoScene scene)
	{
		var bytes = await scene.Asset("rigged_bar.glb");
		var model = GltfLoader.LoadGlb(bytes);
		var prim = model.Primitives[0];
		_char = await _engine.CreateSkinnedMeshAsync(prim.SkinnedVertices!, prim.Indices);
		_charVerts = prim.SkinnedVertices!.Length;
		_skeleton = model.Skeleton!;
		_clip = model.Clips[0];
		_palette = new Matrix4x4[_skeleton.JointCount];
	}

	public RenderGraph Build(DemoScene scene)
	{
		return RenderGraph.Define(g =>
	{
		var screen = g.Backbuffer;
		var albedo = g.Screen.Color("albedo", Format.Rgba8Srgb);
		var normal = g.Screen.Color("normal", Format.Rgba16F);
		var position = g.Screen.Color("position", Format.Rgba16F);
		var depth = g.Screen.Depth("depth", Format.Depth32F);
		var lit = g.Screen.Color("lit", Format.Rgba16F);
		var graded = g.Screen.Color("graded", Format.Rgba16F);
		var skinned = g.Buffer("skinned", _charVerts, VertexLayout.MeshStandard);
		_viewproj = g.Constants<Matrix4x4>("stylized.viewproj");
		_csmSplits = g.Constants<DemoGraph.CsmSplits>("csm.splits");

		var cascadeMaps = new DepthHandle[CascadeCount];
		for (var c = 0; c < CascadeCount; c++)
		{
			cascadeMaps[c] = g.Domain($"sun_csm{c}", (int)ShadowResolution, (int)ShadowResolution).Depth($"csm_depth{c}", Format.Depth32F);
			_cascadeViewproj[c] = g.Constants<Matrix4x4>($"stylized.cascade{c}.viewproj");
		}

		// Skin the character once.
		g.Compute("skin")
			.ReadWrites(skinned)
			.Dispatch(ctx =>
			{
				var t = _clip.Duration > 0f ? (float)ctx.Time % _clip.Duration : 0f;
				AnimationSampler.Evaluate(_skeleton, _clip, t, _palette);
				ctx.DispatchSkin("skin", _char, _palette, skinned, _charVerts);
			});

		// Cascade depth: the skinned character + the ground (one pre-skin feeds all three).
		for (var c = 0; c < CascadeCount; c++)
		{
			var cascade = c;
			g.Pass($"shadow{c}")
				.Reads(_cascadeViewproj[cascade])
				.WritesDepth(cascadeMaps[cascade])
				.ReadsVertex(skinned)
				.Draw(ctx =>
				{
					DemoDraw.Skinned(ctx, _char, skinned, "depthonly", Matrix4x4.Identity);
					DemoDraw.Mesh(ctx, _ground, "depthonly", Matrix4x4.Identity);
				});
		}

		// G-buffer: skinned character + ground + the windy foliage field.
		g.Pass("gbuffer")
			.Reads(_viewproj)
			.Writes(albedo, normal, position)
			.WritesDepth(depth)
			.ReadsVertex(skinned)
			.Draw(ctx =>
			{
				DemoDraw.Skinned(ctx, _char, skinned, "gbuffer", Matrix4x4.Identity);
				DemoDraw.Mesh(ctx, _ground, "gbuffer", Matrix4x4.Identity);
				_wind[0] = 1f; _wind[1] = 0.35f; _wind[2] = (float)ctx.Time; _wind[3] = 0f;
				_wind[4] = 0.16f; _wind[5] = 2.0f; _wind[6] = 0f; _wind[7] = 0f;
				var foliage = new DrawCall { Mesh = _card, Material = _blade, Technique = "foliage", Instances = DrawCall.From<Blade>(_field) };
				foliage.SetUniforms<float>(_wind);
				ctx.Draw(foliage);
			});

		g.Pass("lighting")
			.Samples(albedo, normal, position, cascadeMaps[0], cascadeMaps[1], cascadeMaps[2])
			.Reads(_csmSplits)
			.Writes(lit)
			.Draw(ctx => ctx.FullScreen("lighting_toon", PackLighting(), albedo, normal, position, cascadeMaps[0], cascadeMaps[1], cascadeMaps[2]));

		g.Pass("sky").Blends(lit).Samples(depth).Draw(ctx => ctx.FullScreen("sky", PackSky(), depth));

		g.Pass("grade")
			.Samples(lit)
			.Writes(graded)
			.Draw(ctx => ctx.FullScreen("grade", [1.06f, 0.98f, 0.82f, 0.7f, 1.1f, 0.9f, 0f, 0f], lit));

		g.Pass("blit").Samples(graded).Writes(screen).Draw(ctx => ctx.FullScreen("blit", graded));
		DemoGraph.Hud(g, screen, scene);
		g.Present(screen);
	});
	}

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

	private static TextureData BuildBladeTexture()
	{
		const int w = 24, h = 48;
		var rgba = new byte[w * h * 4];
		for (var py = 0; py < h; py++)
		{
			var v = py / (float)(h - 1);
			var halfWidth = float.Lerp(0.06f, 0.42f, v);
			var shade = float.Lerp(0.7f, 0.32f, v);
			for (var px = 0; px < w; px++)
			{
				var o = (py * w + px) * 4;
				rgba[o + 0] = (byte)(0.22f * shade * 255);
				rgba[o + 1] = (byte)(shade * 255);
				rgba[o + 2] = (byte)(0.16f * shade * 255);
				rgba[o + 3] = MathF.Abs(px / (float)(w - 1) - 0.5f) < halfWidth ? (byte)255 : (byte)0;
			}
		}
		return TextureData.FromRgba8(w, h, rgba, TextureFormat.Rgba8Srgb, generateMips: false);
	}
}
