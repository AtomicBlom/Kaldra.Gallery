using System.Numerics;
using System.Runtime.InteropServices;
using Loom.Core.Geometry;
using Loom.Core.Graph;
using Loom.Core.Hosting;
using Loom.Core.Materials;

namespace Loom.Sandbox.Demos;

/// <summary>
/// Cascaded shadow maps (M15-3). A cube on a large ground, lit by the sun, shadowed by three cascades fitted to
/// the camera frustum (near→far) so the shadow stays crisp across distance where a single map would smear. The
/// deferred g-buffer feeds a <c>lighting_csm</c> pass that selects a cascade per pixel by view depth and samples
/// that cascade's map; the cascade matrices/splits ride the M15-0 uniform seam. Press the debug toggle to tint
/// pixels by cascade (red/green/blue) — the M15-7 cascade visualization.
/// </summary>
internal sealed class CascadedShadowsDemo : IDemo
{
	public string Name => "Cascaded shadows";

	private const int CascadeCount = 3;
	private const float ShadowResolution = 1024f;
	private const float CamNear = 0.5f;
	private const float CamFar = 60f;
	private static readonly Vector3 SunDir = Vector3.Normalize(new Vector3(0.5f, 0.7f, -0.5f));

	private Mesh _ground;
	private readonly Camera[] _cascades = new Camera[CascadeCount];
	private readonly float[] _splits = new float[CascadeCount];
	private readonly float[] _csmParams = new float[68]; // 3×mat4 (48) + eye + forward + sunDir + sunColor + ambient
	private IEngine _engine = null!;
	private ConstantsHandle _csmSplits = null!;

	public bool DebugTint { get; set; }

	public void DefineTechniques(IEngine engine)
	{
		_engine = engine;
		engine.DefineTechnique("gbuffer", new TechniqueDescriptor("gbuffer", HasVertexInput: true));
		engine.DefineTechnique("depthonly", new TechniqueDescriptor("depthonly", HasVertexInput: true));
		engine.DefineTechnique("lighting_csm", new TechniqueDescriptor("lighting_csm"));
		engine.DefineTechnique("blit", new TechniqueDescriptor("blit"));

		// A large flat ground (±40 on X/Z at y=-0.5, so the unit cube sits on it) — wide + deep enough that the
		// view spans all three cascades.
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

	// A low camera looking across the ground into the distance, so near→far spans the cascades.
	public Camera? GetCamera(float aspect, float time)
	{
		var view = Matrix4x4.CreateLookAt(new Vector3(0f, 4f, 16f), new Vector3(0f, 0f, -8f), Vector3.UnitY);
		var projection = Matrix4x4.CreatePerspectiveFieldOfView(MathF.PI / 3f, aspect, CamNear, CamFar);
		return new Camera(view, projection);
	}

	public (IReadOnlyList<Camera> Cascades, IReadOnlyList<float> Splits)? GetCascades(Camera camera, Vector3 lightDir)
	{
		Cascades.Compute(camera, SunDir, CamNear, CamFar, ShadowResolution, _cascades, _splits);
		// The split far-depths + the cascade-tint flag ride a parameter buffer the lighting pass reads (the matrices
		// stay on the closure push, where the backend's per-cascade clip-space flip applies).
		_engine.Write(_csmSplits, new DemoGraph.CsmSplits(new Vector4(_splits[0], _splits[1], _splits[2], DebugTint ? 1f : 0f)));
		return (_cascades, _splits);
	}

	public void DrawDebugUI()
	{
		var tint = DebugTint;
		if (ImGuiNET.ImGui.Checkbox("Cascade tint (red/green/blue by distance)", ref tint))
			DebugTint = tint;
		ImGuiNET.ImGui.Text($"splits: {_splits[0]:F1} / {_splits[1]:F1} / {_splits[2]:F1}");
	}

	public RenderGraph Build(DemoScene scene) => RenderGraph.Define(g =>
	{
		var screen = g.Backbuffer;
		_csmSplits = g.Constants<DemoGraph.CsmSplits>("csm.splits");
		var albedo = g.Screen.Color("albedo", Format.Rgba8Srgb);
		var normal = g.Screen.Color("normal", Format.Rgba16F);
		var position = g.Screen.Color("position", Format.Rgba16F);
		var depth = g.Screen.Depth("depth", Format.Depth32F);
		var lit = g.Screen.Color("lit", Format.Rgba16F);

		// One 1024² depth domain per cascade.
		var cascadeMaps = new DepthHandle[CascadeCount];
		for (var c = 0; c < CascadeCount; c++)
		{
			var dom = g.Domain($"sun_csm{c}", (int)ShadowResolution, (int)ShadowResolution);
			cascadeMaps[c] = dom.Depth($"csm_depth{c}", Format.Depth32F);
		}

		// A depth-only pass per cascade, drawing the scene from that cascade's sun view.
		for (var c = 0; c < CascadeCount; c++)
		{
			var cascade = c; // capture by value
			g.Pass($"shadow{c}")
				.WritesDepth(cascadeMaps[cascade])
				.Draw(ctx =>
				{
					ctx.Draw(scene.Cube, "depthonly", ctx.Cascades[cascade]);
					ctx.Draw(_ground, "depthonly", ctx.Cascades[cascade]);
				});
		}

		g.Pass("gbuffer")
			.Writes(albedo, normal, position)
			.WritesDepth(depth)
			.Draw(ctx =>
			{
				ctx.Draw(scene.Cube, "gbuffer");
				ctx.Draw(_ground, "gbuffer");
			});

		g.Pass("lighting")
			.Samples(albedo, normal, position, cascadeMaps[0], cascadeMaps[1], cascadeMaps[2])
			.Reads(_csmSplits)
			.Writes(lit)
			.Draw(ctx => ctx.FullScreen("lighting_csm", PackParams(ctx), albedo, normal, position, cascadeMaps[0], cascadeMaps[1], cascadeMaps[2]));

		g.Pass("blit").Samples(lit).Writes(screen).Draw(ctx => ctx.FullScreen("blit", lit));
		DemoGraph.Hud(g, screen);
		g.Present(screen);
	});

	// Pack the per-frame CSM uniform block: three cascade view-projections, the split far-depths (+ debug flag),
	// the camera eye + forward (for the view-depth metric), and the sun direction + key intensity.
	private ReadOnlySpan<float> PackParams(IRenderContext ctx)
	{
		var p = _csmParams;
		for (var c = 0; c < CascadeCount; c++)
		{
			var m = ctx.Cascades[c].ViewProjection;
			MemoryMarshal.CreateReadOnlySpan(in m.M11, 16).CopyTo(p.AsSpan(c * 16, 16));
		}
		// Splits + the cascade-tint flag now ride the CsmSplits parameter buffer (written in GetCascades); the push
		// carries only the clip-space matrices + the camera/sun/ambient data the engine can't host-write.
		Matrix4x4.Invert(ctx.Camera.View, out var invView);
		var eye = invView.Translation;
		var forward = -Vector3.Normalize(new Vector3(invView.M31, invView.M32, invView.M33));
		p[48] = eye.X; p[49] = eye.Y; p[50] = eye.Z; p[51] = 0f;
		p[52] = forward.X; p[53] = forward.Y; p[54] = forward.Z; p[55] = 0f;
		p[56] = SunDir.X; p[57] = SunDir.Y; p[58] = SunDir.Z; p[59] = 1.8f; // sun dir + key intensity
		p[60] = 1f; p[61] = 1f; p[62] = 1f; p[63] = 0f;                     // white sun light
		p[64] = 0.15f; p[65] = 0.15f; p[66] = 0.15f; p[67] = 0f;            // flat grey ambient
		return p;
	}
}
