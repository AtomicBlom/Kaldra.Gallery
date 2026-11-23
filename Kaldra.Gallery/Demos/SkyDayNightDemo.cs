using System.Numerics;
using System.Runtime.InteropServices;

using BinaryVibrance.Kaldra.Core.Geometry;
using BinaryVibrance.Kaldra.Core.Graph;
using BinaryVibrance.Kaldra.Core.Hosting;
using BinaryVibrance.Kaldra.Core.Materials;

using BinaryVibrance.Vysara;
using BinaryVibrance.Vysara.Controls;

namespace Kaldra.Gallery.Demos;

/// <summary>
/// Sky + day/night + dynamic ambient. The cascaded-shadow scene under a time-of-day sun: the sun direction and
/// the sun/ambient/sky colours all come from <see cref="DayNight"/> (warm low sun + orange horizon at golden
/// hour, white sun + blue sky at noon, dim blue night), recomputed each frame and threaded through the per-frame
/// uniform seam. A sky pass fills the background with an analytic gradient + sun glow. The cascades are refit
/// to the moving sun every frame; under <c>--capture</c> the frozen time pins a deterministic golden-hour pose.
/// </summary>
internal sealed class SkyDayNightDemo : IDemo
{
	public string Name => "Sky + day/night";

	private const int CascadeCount = 3;
	private const float ShadowResolution = 1024f;
	private const float CamNear = 0.5f;
	private const float CamFar = 60f;

	private Mesh _ground;
	private DayNight.State _sun = DayNight.Compute(0.47f);
	private readonly Concepts.Camera[] _cascades = new Concepts.Camera[CascadeCount];
	private readonly float[] _splits = new float[CascadeCount];
	private readonly float[] _csmParams = new float[68];
	private readonly float[] _skyParams = new float[36];
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
		engine.DefineTechnique("gbuffer", new TechniqueDescriptor("gbuffer", HasVertexInput: true));
		engine.DefineTechnique("depthonly", new TechniqueDescriptor("depthonly", HasVertexInput: true));
		engine.DefineTechnique("lighting_csm", new TechniqueDescriptor("lighting_csm"));
		engine.DefineTechnique("sky", new TechniqueDescriptor("sky", Blend: BlendMode.AlphaOver));
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

	// GetCamera runs once per frame before Update, so it doubles as the per-frame sun update: the day phase
	// advances with time (frozen under capture → a deterministic golden hour).
	private float _phaseOverride = -1f; // < 0 = advance with time; ≥ 0 = scrubbed time-of-day

	public Concepts.Camera? GetCamera(float aspect, float time)
	{
		_sun = DayNight.Compute(_phaseOverride >= 0f ? _phaseOverride : 0.47f + 0.02f * time);
		// Low eye looking slightly up across the ground toward the sun (upper-left), so the sky + horizon + sun
		// glow fill the upper half of the frame.
		return _cam
			.Perspective(MathF.PI / 3f, aspect, CamNear, CamFar)
			.LookAt(new Vector3(2f, 2.2f, 16f), new Vector3(-4f, 4f, -20f));
	}

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
		if (_sunHeightText is not null)
			_sunHeightText.Text = $"sun height: {_sun.SunDir.Y:F2}";
	}

	private TextBlock? _sunHeightText;
	private Slider? _phaseSlider;
	private UIElement? _debugPanel;

	public UIElement? DebugPanel => _debugPanel ??= BuildDebugPanel();

	private UIElement BuildDebugPanel()
	{
		var scrub = new CheckBox { Content = "Scrub time of day", IsChecked = _phaseOverride >= 0f };
		_phaseSlider = new Slider { Minimum = 0f, Maximum = 1f, Value = _phaseOverride >= 0f ? _phaseOverride : 0.25f, Visibility = _phaseOverride >= 0f ? Visibility.Visible : Visibility.Collapsed };
		scrub.Checked += (_, _) => { _phaseOverride = _phaseSlider!.Value >= 0f ? (float)_phaseSlider.Value : 0.25f; _phaseSlider.Visibility = Visibility.Visible; };
		scrub.Unchecked += (_, _) => { _phaseOverride = -1f; _phaseSlider!.Visibility = Visibility.Collapsed; };
		_phaseSlider.ValueChanged += (_, _) => _phaseOverride = (float)_phaseSlider.Value;
		_sunHeightText = new TextBlock { Text = $"sun height: {_sun.SunDir.Y:F2}", Margin = new Thickness(0f, 6f, 0f, 0f) };
		var panel = new StackPanel();
		panel.Children.Add(scrub);
		panel.Children.Add(_phaseSlider);
		panel.Children.Add(_sunHeightText);
		return panel;
	}

	public RenderGraph Build(DemoScene scene) => RenderGraph.Define(g =>
	{
		var screen = g.Backbuffer;
		var albedo = g.Screen.Color("albedo", Format.Rgba8Srgb);
		var normal = g.Screen.Color("normal", Format.Rgba16F);
		var position = g.Screen.Color("position", Format.Rgba16F);
		var depth = g.Screen.Depth("depth", Format.Depth32F);
		var lit = g.Screen.Color("lit", Format.Rgba16F);
		_viewproj = g.Constants<Matrix4x4>("skydaynight.viewproj");
		_csmSplits = g.Constants<DemoGraph.CsmSplits>("csm.splits");

		var cascadeMaps = new DepthHandle[CascadeCount];
		for (var c = 0; c < CascadeCount; c++)
		{
			cascadeMaps[c] = g.Domain($"sun_csm{c}", (int)ShadowResolution, (int)ShadowResolution).Depth($"csm_depth{c}", Format.Depth32F);
			_cascadeViewproj[c] = g.Constants<Matrix4x4>($"skydaynight.cascade{c}.viewproj");
		}

		for (var c = 0; c < CascadeCount; c++)
		{
			var cascade = c;
			g.Pass($"shadow{c}")
				.Reads(_cascadeViewproj[cascade])
				.WritesDepth(cascadeMaps[cascade])
				.Draw(ctx =>
				{
					DemoDraw.Mesh(ctx, scene.Cube, "depthonly", Matrix4x4.Identity);
					DemoDraw.Mesh(ctx, _ground, "depthonly", Matrix4x4.Identity);
				});
		}

		g.Pass("gbuffer")
			.Reads(_viewproj)
			.Writes(albedo, normal, position)
			.WritesDepth(depth)
			.Draw(ctx =>
			{
				DemoDraw.Mesh(ctx, scene.Cube, "gbuffer", Matrix4x4.Identity);
				DemoDraw.Mesh(ctx, _ground, "gbuffer", Matrix4x4.Identity);
			});

		g.Pass("lighting")
			.Samples(albedo, normal, position, cascadeMaps[0], cascadeMaps[1], cascadeMaps[2])
			.Reads(_csmSplits)
			.Writes(lit)
			.Draw(ctx => ctx.FullScreen("lighting_csm", PackLighting(), albedo, normal, position, cascadeMaps[0], cascadeMaps[1], cascadeMaps[2]));

		// Sky fills the background (alpha-over keeps lit geometry, fills the rest with the gradient + sun glow).
		// Masks on scene depth == cleared far (no geometry); position.a can't tell — colour clears to alpha 1.
		g.Pass("sky")
			.Blends(lit)
			.Samples(depth)
			.Draw(ctx => ctx.FullScreen("sky", PackSky(), depth));

		g.Pass("blit").Samples(lit).Writes(screen).Draw(ctx => ctx.FullScreen("blit", lit));
		DemoGraph.Hud(g, screen, scene);
		g.Present(screen);
	});

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
		p[32] = _sun.SunColor.X; p[33] = _sun.SunColor.Y; p[34] = _sun.SunColor.Z; p[35] = 220f; // glow sharpness
		return p;
	}
}
