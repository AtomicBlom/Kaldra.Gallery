using System.Numerics;
using System.Runtime.InteropServices;

using Loom.Core;
using Loom.Core.Geometry;
using Loom.Core.Graph;
using Loom.Core.Hosting;
using Loom.Core.Materials;

namespace Loom.Sandbox.Demos;

/// <summary>
/// Toon/ramp lighting + colour grading (M15-6). The cascaded-shadow + day/night scene shaded with the cel
/// <c>lighting_toon</c> technique (banded diffuse + rim, a drop-in sibling of <c>lighting_csm</c>), then graded
/// by a fullscreen <c>grade</c> pass — here a warm, slightly-desaturated "autumn" look (the season-tint hook).
/// </summary>
internal sealed class ToonDemo : IDemo
{
	public string Name => "Toon + grading";

	private const int CascadeCount = 3;
	private const float ShadowResolution = 1024f;
	private const float CamNear = 0.5f;
	private const float CamFar = 60f;

	private Mesh _ground;
	private DayNight.State _sun = DayNight.Compute(0.3f);
	private readonly Camera[] _cascades = new Camera[CascadeCount];
	private readonly float[] _splits = new float[CascadeCount];
	private readonly float[] _csmParams = new float[68];
	private IEngine _engine = null!;
	private ConstantsHandle _csmSplits = null!;

	public void DefineTechniques(IEngine engine)
	{
		_engine = engine;
		engine.DefineTechnique("gbuffer", new TechniqueDescriptor("gbuffer", HasVertexInput: true));
		engine.DefineTechnique("depthonly", new TechniqueDescriptor("depthonly", HasVertexInput: true));
		engine.DefineTechnique("lighting_toon", new TechniqueDescriptor("lighting_toon"));
		engine.DefineTechnique("sky", new TechniqueDescriptor("sky", Blend: BlendMode.AlphaOver));
		engine.DefineTechnique("grade", new TechniqueDescriptor("grade", HasFragmentPush: true));
		engine.DefineTechnique("blit", new TechniqueDescriptor("blit"));

		const float s = 40f, y = -0.5f;
		var up = Vector3.UnitY;
		var tan = new Vector4(1, 0, 0, 1);
		Vertex[] vertices =
		[
			new(new Vector3(-s, y, -s), up, new Vector2(0, 0), tan),
			new(new Vector3(s, y, -s), up, new Vector2(1, 0), tan),
			new(new Vector3(s, y, s), up, new Vector2(1, 1), tan),
			new(new Vector3(-s, y, s), up, new Vector2(0, 1), tan),
		];
		_ground = engine.CreateMesh(vertices, [0, 1, 2, 0, 2, 3]);
	}

	public Camera? GetCamera(float aspect, float time)
	{
		_sun = DayNight.Compute(0.3f); // mid-morning: white-ish sun, blue sky — so the toon banding reads clearly
		var view = Matrix4x4.CreateLookAt(new Vector3(3f, 2.5f, 7f), new Vector3(0f, 0f, -2f), Vector3.UnitY);
		var projection = Matrix4x4.CreatePerspectiveFieldOfView(MathF.PI / 3f, aspect, CamNear, CamFar);
		return new Camera(view, projection);
	}

	public (IReadOnlyList<Camera> Cascades, IReadOnlyList<float> Splits)? GetCascades(Camera camera, Vector3 lightDir)
	{
		Cascades.Compute(camera, _sun.SunDir, CamNear, CamFar, ShadowResolution, _cascades, _splits);
		// Splits ride a parameter buffer the lighting pass reads; the matrices stay on the closure push (clip-space flip).
		_engine.Write(_csmSplits, new DemoGraph.CsmSplits(new Vector4(_splits[0], _splits[1], _splits[2], 0f)));
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

		var cascadeMaps = new DepthHandle[CascadeCount];
		for (var c = 0; c < CascadeCount; c++)
			cascadeMaps[c] = g.Domain($"sun_csm{c}", (int)ShadowResolution, (int)ShadowResolution).Depth($"csm_depth{c}", Format.Depth32F);

		for (var c = 0; c < CascadeCount; c++)
		{
			var cascade = c;
			g.Pass($"shadow{c}")
				.WritesDepth(cascadeMaps[cascade])
				.Draw(ctx =>
				{
					ctx.Draw(new DrawCall { Mesh = scene.Cube, Technique = "depthonly", View = ctx.Views[ViewKeys.Cascade(cascade)] });
					ctx.Draw(new DrawCall { Mesh = _ground, Technique = "depthonly", View = ctx.Views[ViewKeys.Cascade(cascade)] });
				});
		}

		g.Pass("gbuffer")
			.Writes(albedo, normal, position)
			.WritesDepth(depth)
			.Draw(ctx =>
			{
				ctx.Draw(DrawCall.Create(scene.Cube, "gbuffer"));
				ctx.Draw(DrawCall.Create(_ground, "gbuffer"));
			});

		_csmSplits = g.Constants<DemoGraph.CsmSplits>("csm.splits");
		g.Pass("lighting")
			.Samples(albedo, normal, position, cascadeMaps[0], cascadeMaps[1], cascadeMaps[2])
			.Reads(_csmSplits)
			.Writes(lit)
			.Draw(ctx => ctx.FullScreen("lighting_toon", PackLighting(ctx), albedo, normal, position, cascadeMaps[0], cascadeMaps[1], cascadeMaps[2]));

		g.Pass("sky")
			.Blends(lit)
			.Samples(depth)
			.Draw(ctx => ctx.FullScreen("sky", PackSky(ctx), depth));

		// Warm, slightly-desaturated autumn grade (tint.rgb, strength; contrast, saturation).
		g.Pass("grade")
			.Samples(lit)
			.Writes(graded)
			.Draw(ctx => ctx.FullScreen("grade", [1.08f, 0.94f, 0.72f, 0.8f, 1.12f, 0.82f, 0f, 0f], lit));

		g.Pass("blit").Samples(graded).Writes(screen).Draw(ctx => ctx.FullScreen("blit", graded));
		DemoGraph.Hud(g, screen);
		g.Present(screen);
	});

	private ReadOnlySpan<float> PackLighting(IRenderContext ctx)
	{
		var p = _csmParams;
		for (var c = 0; c < CascadeCount; c++)
		{
			var m = ctx.Views[ViewKeys.Cascade(c)].ViewProjectionMatrix;
			MemoryMarshal.CreateReadOnlySpan(in m.M11, 16).CopyTo(p.AsSpan(c * 16, 16));
		}
		// Splits ride the CsmSplits parameter buffer (written in GetCascades); the push carries matrices + camera/sun/ambient.
		Matrix4x4.Invert(ctx.View.ViewMatrix, out var invView);
		var eye = invView.Translation;
		var forward = -Vector3.Normalize(new Vector3(invView.M31, invView.M32, invView.M33));
		p[48] = eye.X; p[49] = eye.Y; p[50] = eye.Z; p[51] = 0f;
		p[52] = forward.X; p[53] = forward.Y; p[54] = forward.Z; p[55] = 0f;
		p[56] = _sun.SunDir.X; p[57] = _sun.SunDir.Y; p[58] = _sun.SunDir.Z; p[59] = _sun.Intensity;
		p[60] = _sun.SunColor.X; p[61] = _sun.SunColor.Y; p[62] = _sun.SunColor.Z; p[63] = 0f;
		p[64] = _sun.Ambient.X; p[65] = _sun.Ambient.Y; p[66] = _sun.Ambient.Z; p[67] = 0f;
		return p;
	}

	private readonly float[] _skyParams = new float[36];

	private ReadOnlySpan<float> PackSky(IRenderContext ctx)
	{
		var p = _skyParams;
		Matrix4x4.Invert(ctx.View.ViewProjectionMatrix, out var invVp);
		MemoryMarshal.CreateReadOnlySpan(in invVp.M11, 16).CopyTo(p.AsSpan(0, 16));
		Matrix4x4.Invert(ctx.View.ViewMatrix, out var invView);
		var eye = invView.Translation;
		p[16] = eye.X; p[17] = eye.Y; p[18] = eye.Z; p[19] = 0f;
		p[20] = _sun.SunDir.X; p[21] = _sun.SunDir.Y; p[22] = _sun.SunDir.Z; p[23] = 0f;
		p[24] = _sun.Zenith.X; p[25] = _sun.Zenith.Y; p[26] = _sun.Zenith.Z; p[27] = 0f;
		p[28] = _sun.Horizon.X; p[29] = _sun.Horizon.Y; p[30] = _sun.Horizon.Z; p[31] = 0f;
		p[32] = _sun.SunColor.X; p[33] = _sun.SunColor.Y; p[34] = _sun.SunColor.Z; p[35] = 220f;
		return p;
	}
}
