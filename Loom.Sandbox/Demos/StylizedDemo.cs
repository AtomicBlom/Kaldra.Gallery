using System.Numerics;
using System.Runtime.InteropServices;

using Loom.Assets;
using Loom.Core.Animation;
using Loom.Core.Geometry;
using Loom.Core.Graph;
using Loom.Core.Hosting;
using Loom.Core.Materials;

namespace Loom.Sandbox.Demos;

/// <summary>
/// Track B — stylized 3D reference frame (M15-8). Composes the whole milestone end to end: a compute-pre-skinned
/// animated glTF character (M15-1/2) in a windy alpha-tested foliage field (M15-5), under cascaded sun shadows
/// (M15-3) and a day/night sky (M15-4), shaded with cel/toon deferred lighting (M15-6) and a warm season grade.
/// Pass order: skin compute → N cascade depth (char + ground) → g-buffer (char + ground + foliage) → sky →
/// toon lighting → grade → blit. The skinned buffer is produced once and read by every depth + g-buffer pass —
/// the "skin once, read many" payoff the render graph derives. Frozen time pins a deterministic cross-backend
/// golden (the M15-8 acceptance).
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
	private readonly Camera[] _cascades = new Camera[CascadeCount];
	private readonly float[] _splits = new float[CascadeCount];
	private readonly float[] _csmParams = new float[72];
	private readonly float[] _skyParams = new float[36];
	private readonly float[] _wind = new float[8];

	public void DefineTechniques(IEngine engine)
	{
		engine.DefineTechnique("skin", new TechniqueDescriptor("skin", VertexEntry: "main", FragmentEntry: "main", IsCompute: true, HasFragmentPush: true));
		engine.DefineTechnique("depthonly", new TechniqueDescriptor("depthonly", HasVertexInput: true));
		engine.DefineTechnique("gbuffer", new TechniqueDescriptor("gbuffer", HasVertexInput: true));
		engine.DefineTechnique("foliage", new TechniqueDescriptor("foliage", HasVertexInput: true, Layout: VertexLayout.MeshStandard, Instance: VertexLayout.Foliage));
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

		// The rigged character (compute-skinned), scaled up a touch and standing on the ground.
		var model = GltfLoader.Load(Path.Combine(AppContext.BaseDirectory, "assets", "rigged_bar.glb"));
		var prim = model.Primitives[0];
		_char = engine.CreateSkinnedMesh(prim.SkinnedVertices!, prim.Indices);
		_charVerts = prim.SkinnedVertices!.Length;
		_skeleton = model.Skeleton!;
		_clip = model.Clips[0];
		_palette = new Matrix4x4[_skeleton.JointCount];

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

	public Camera? GetCamera(float aspect, float time)
	{
		var view = Matrix4x4.CreateLookAt(new Vector3(4.5f, 3.2f, 8.5f), new Vector3(0f, 1.1f, 0f), Vector3.UnitY);
		var projection = Matrix4x4.CreatePerspectiveFieldOfView(MathF.PI / 3f, aspect, CamNear, CamFar);
		return new Camera(view, projection);
	}

	public (IReadOnlyList<Camera> Cascades, IReadOnlyList<float> Splits)? GetCascades(Camera camera, Vector3 lightDir)
	{
		Cascades.Compute(camera, _sun.SunDir, CamNear, CamFar, ShadowResolution, _cascades, _splits);
		return (_cascades, _splits);
	}

	public RenderGraph Build(DemoScene scene) => RenderGraph.Define(g =>
	{
		var screen = g.Backbuffer;
		var albedo = g.Screen.Color("albedo", Format.Rgba8Srgb);
		var normal = g.Screen.Color("normal", Format.Rgba16F);
		var position = g.Screen.Color("position", Format.Rgba16F);
		var depth = g.Screen.Depth("depth", Format.Depth32F);
		var lit = g.Screen.Color("lit", Format.Rgba16F);
		var graded = g.Screen.Color("graded", Format.Rgba16F);
		var skinned = g.Buffer("skinned", _charVerts * 48);

		var cascadeMaps = new DepthHandle[CascadeCount];
		for (var c = 0; c < CascadeCount; c++)
			cascadeMaps[c] = g.Domain($"sun_csm{c}", (int)ShadowResolution, (int)ShadowResolution).Depth($"csm_depth{c}", Format.Depth32F);

		// Skin the character once.
		g.Compute("skin")
			.ReadWrites(skinned)
			.Dispatch(ctx =>
			{
				var t = _clip.Duration > 0f ? ctx.Time % _clip.Duration : 0f;
				AnimationSampler.Evaluate(_skeleton, _clip, t, _palette);
				ctx.DispatchSkin("skin", _char, _palette, skinned, _charVerts);
			});

		// Cascade depth: the skinned character + the ground (one pre-skin feeds all three).
		for (var c = 0; c < CascadeCount; c++)
		{
			var cascade = c;
			g.Pass($"shadow{c}")
				.WritesDepth(cascadeMaps[cascade])
				.ReadsVertex(skinned)
				.Draw(ctx =>
				{
					ctx.DrawSkinned(_char, skinned, "depthonly", ctx.Cascades[cascade]);
					ctx.Draw(_ground, "depthonly", ctx.Cascades[cascade]);
				});
		}

		// G-buffer: skinned character + ground + the windy foliage field.
		g.Pass("gbuffer")
			.Writes(albedo, normal, position)
			.WritesDepth(depth)
			.ReadsVertex(skinned)
			.Draw(ctx =>
			{
				ctx.DrawSkinned(_char, skinned, "gbuffer");
				ctx.Draw(_ground, "gbuffer");
				_wind[0] = 1f; _wind[1] = 0.35f; _wind[2] = ctx.Time; _wind[3] = 0f;
				_wind[4] = 0.16f; _wind[5] = 2.0f; _wind[6] = 0f; _wind[7] = 0f;
				ctx.DrawInstanced<Blade>(_card, _blade, "foliage", _field, _wind);
			});

		g.Pass("lighting")
			.Samples(albedo, normal, position, cascadeMaps[0], cascadeMaps[1], cascadeMaps[2])
			.Writes(lit)
			.Draw(ctx => ctx.FullScreen("lighting_toon", PackLighting(ctx), albedo, normal, position, cascadeMaps[0], cascadeMaps[1], cascadeMaps[2]));

		g.Pass("sky").Blends(lit).Samples(depth).Draw(ctx => ctx.FullScreen("sky", PackSky(ctx), depth));

		g.Pass("grade")
			.Samples(lit)
			.Writes(graded)
			.Draw(ctx => ctx.FullScreen("grade", [1.06f, 0.98f, 0.82f, 0.7f, 1.1f, 0.9f, 0f, 0f], lit));

		g.Pass("blit").Samples(graded).Writes(screen).Draw(ctx => ctx.FullScreen("blit", graded));
		DemoGraph.Hud(g, screen);
		g.Present(screen);
	});

	private ReadOnlySpan<float> PackLighting(IRenderContext ctx)
	{
		var p = _csmParams;
		for (var c = 0; c < CascadeCount; c++)
		{
			var m = ctx.Cascades[c].ViewProjection;
			MemoryMarshal.CreateReadOnlySpan(in m.M11, 16).CopyTo(p.AsSpan(c * 16, 16));
		}
		p[48] = ctx.CascadeSplits[0]; p[49] = ctx.CascadeSplits[1]; p[50] = ctx.CascadeSplits[2]; p[51] = 0f;
		Matrix4x4.Invert(ctx.Camera.View, out var invView);
		var eye = invView.Translation;
		var forward = -Vector3.Normalize(new Vector3(invView.M31, invView.M32, invView.M33));
		p[52] = eye.X; p[53] = eye.Y; p[54] = eye.Z; p[55] = 0f;
		p[56] = forward.X; p[57] = forward.Y; p[58] = forward.Z; p[59] = 0f;
		p[60] = _sun.SunDir.X; p[61] = _sun.SunDir.Y; p[62] = _sun.SunDir.Z; p[63] = _sun.Intensity;
		p[64] = _sun.SunColor.X; p[65] = _sun.SunColor.Y; p[66] = _sun.SunColor.Z; p[67] = 0f;
		p[68] = _sun.Ambient.X; p[69] = _sun.Ambient.Y; p[70] = _sun.Ambient.Z; p[71] = 0f;
		return p;
	}

	private ReadOnlySpan<float> PackSky(IRenderContext ctx)
	{
		var p = _skyParams;
		Matrix4x4.Invert(ctx.Camera.ViewProjection, out var invVp);
		MemoryMarshal.CreateReadOnlySpan(in invVp.M11, 16).CopyTo(p.AsSpan(0, 16));
		Matrix4x4.Invert(ctx.Camera.View, out var invView);
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
