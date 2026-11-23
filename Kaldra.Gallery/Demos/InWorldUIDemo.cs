using System.Numerics;

using BinaryVibrance.Kaldra.Concepts;
using BinaryVibrance.Kaldra.Core.Geometry;
using BinaryVibrance.Kaldra.Core.Graph;
using BinaryVibrance.Kaldra.Core.Hosting;
using BinaryVibrance.Kaldra.Core.Materials;
#if KALDRA_WEB
using Kaldra.Web.UI;
#else
using Kaldra.Gallery.UI;
#endif
using BinaryVibrance.Kaldra.UI.Vysara;
using BinaryVibrance.Vysara;
using BinaryVibrance.Vysara.Controls;
using BinaryVibrance.Vysara.Input;
using BinaryVibrance.Vysara.Media;
using BinaryVibrance.Vysara.Shapes;

namespace Kaldra.Gallery.Demos;

/// <summary>
/// Demonstrates in-world UI placement: a Vysara panel rendered into an offscreen texture and mapped onto a
/// world-space quad, with screen-space pointer events projected onto the quad's surface so clicks reach the
/// in-world buttons. The panel is an independent <see cref="InWorldPanel"/> whose dedicated <c>UIHost</c>
/// never sees the 3D world — all projection logic lives in the bridge.
/// <para>
/// Beside it, the other way to put UI in the world: <see cref="ScreenAnchors"/> keeps screen-space labels pinned to
/// points in the scene — the cube's base corners and the ground's corners — moving them each frame to where the camera
/// puts their points, and fading out those whose points leave the frame. The labels stay crisp and upright at any
/// distance, where the panel is drawn into the scene and turns with it. They are drawn over the frame, so the scene does
/// not hide them.
/// </para>
/// </summary>
internal sealed class InWorldUIDemo : IDemo
{
	private InWorldPanel? _panel;
	private InWorldPanelView? _panelView;
	private Mesh? _panelQuad;
	private ConstantsHandle _viewproj = null!;
	private IEngine _engine = null!;
	// Screen-space labels pinned to points in the scene, on a host of their own: they move every frame, and a host of
	// their own lets this demo place them against this frame's camera, after the shell's host has already composited.
	private UIHost? _anchorHost;
	private UIPainter? _anchorPainter;
	private ScreenAnchors? _anchors;

	public string Name => "In-World UI";

	public void DefineTechniques(IEngine engine)
	{
		_engine = engine;
		engine.DefineTechnique("lit", new TechniqueDescriptor("lit", HasVertexInput: true));
		engine.DefineTechnique("ui_in_world", new TechniqueDescriptor("ui_in_world",
			HasVertexInput: true, LinearFilter: true, Blend: BlendMode.PremultipliedAlpha));

		// A centered unit quad in the XY plane (±0.5), facing +Z. Vertices span [-0.5, 0.5]² so the
		// WorldTransform's scale directly sets the panel's physical world dimensions. UV covers [0,1]²
		// mapping (0,0)=top-left, (1,1)=bottom-right to match Vysara's compositor output.
		var n = new Vector3(0, 0, 1);
		var tan = new Vector4(1, 0, 0, 1);
		Vertex[] verts =
		[
			new(new Vector3(-0.5f, -0.5f, 0f), n, new Vector2(0f, 1f), tan), // bottom-left
			new(new Vector3( 0.5f, -0.5f, 0f), n, new Vector2(1f, 1f), tan), // bottom-right
			new(new Vector3( 0.5f,  0.5f, 0f), n, new Vector2(1f, 0f), tan), // top-right
			new(new Vector3(-0.5f,  0.5f, 0f), n, new Vector2(0f, 0f), tan), // top-left
		];
		_panelQuad = engine.CreateMesh(verts, [0u, 1u, 2u, 0u, 2u, 3u]);

		// Place the panel 2 m wide × 1 m tall, hovering above the scene's origin. Billboard keeps it facing
		// the camera at all orbit angles, so the captured frame always shows the front face.
		var worldTransform = Matrix4x4.CreateScale(2f, 1f, 1f)
						   * Matrix4x4.CreateTranslation(0f, 0.8f, 0f);
		_panel = InWorldPanel.Create(engine, pixelWidth: 512, pixelHeight: 256,
			worldTransform: worldTransform, billboard: true);
		_panelView = new InWorldPanelView();
		_panel.View.Root.Content = _panelView;

		// Labels on the cube's four base corners and the ground's four corners, each the middle of its bottom edge lifted
		// clear of a dot centred on the point. The camera orbits, so ground corners leave and re-enter the frame, fading
		// out and back in as they do.
		_anchorHost = new UIHost();
		_anchorPainter = UIPainter.Create(engine, _anchorHost);
		_anchors = ScreenAnchors.Create(engine, _anchorHost.CreateRoot(UILayer.Default));
		ReadOnlySpan<(Vector3 Point, string Name)> corners =
		[
			(new(-0.5f, -0.5f, 0.5f), "-x +z"), (new(0.5f, -0.5f, 0.5f), "+x +z"),
			(new(0.5f, -0.5f, -0.5f), "+x -z"), (new(-0.5f, -0.5f, -0.5f), "-x -z"),
			(new(-4f, -0.5f, 4f), "ground -x +z"), (new(4f, -0.5f, 4f), "ground +x +z"),
			(new(4f, -0.5f, -4f), "ground +x -z"), (new(-4f, -0.5f, -4f), "ground -x -z"),
		];
		foreach (var (point, name) in corners)
		{
			_anchors.Add(AnchorDot(), point, new Vector2(0.5f, 0.5f));
			_anchors.Add(AnchorLabel(name), point, new Vector2(0.5f, 1f), new Vector2(0f, -8f));
		}
	}

	public RenderGraph Build(DemoScene scene) => RenderGraph.Define(g =>
	{
		var screen = g.Backbuffer;
		var depth = g.Screen.Depth("depth", Format.Depth32F);
		_viewproj = g.Constants<Matrix4x4>("inworld.viewproj");

		if (_panel is null)
		{
			DemoGraph.Hud(g, screen, scene);
			g.Present(screen);
			return;
		}

		// Offscreen panel texture — independent resolution from the backbuffer.
		var panelTex = g.Domain("ui_panel", _panel.PixelWidth, _panel.PixelHeight)
						.Color("ui_panel_color", Format.Rgba8Srgb);

		// 1. Composite the Vysara tree into the offscreen panel texture.
		g.Pass("ui.panel")
			.Writes(panelTex)
			.Draw(ctx => _panel?.Draw(ctx));

		// 2. Forward world pass: ground plane + the world-space panel quad sampling the composited texture.
		// The panel quad uses DrawCall.Sampled to bind panelTex at binding slot 0, ahead of the UBOs — the
		// "sampled graph resource" path in the backend, distinct from material textures.
		g.Pass("world")
			.Reads(_viewproj)
			.Writes(screen)
			.WritesDepth(depth)
			.Samples(panelTex)
			.Draw(ctx =>
			{
				DemoDraw.Mesh(ctx, scene.Cube, "lit", Matrix4x4.Identity);
				DemoDraw.Mesh(ctx, scene.Plane, "lit", Matrix4x4.Identity);
				if (_panelQuad is { } quad && _panel is { } panel)
				{
					var block = new PerDraw { Model = panel.WorldTransform, ObjectId = 0 };
					var call = new DrawCall { Mesh = quad, Technique = "ui_in_world" };
					call.SetUniforms(ref block);
					call.Sampled = new ResourceHandle[] { panelTex };
					ctx.Draw(call);
				}
			});

		// 3. The anchored labels over the lit frame, under the shell's HUD.
		g.Pass("ui.anchors")
			.Blends(screen)
			.Draw(ctx =>
			{
				if (_anchorPainter is { } painter && _anchors is { } anchors)
					painter.Draw(ctx, anchors.View.Surface);
			});

		DemoGraph.Hud(g, screen, scene);
		g.Present(screen);
	});

	public void Update(Camera camera) => _engine.Write(_viewproj, camera.ViewProjection);

	public void OnPointer(in PointerInput e) => _panel?.QueueScreenPointer(e);

	public void PreFrame(double deltaSeconds, float time, Camera camera, Vector2 framebufferSize)
	{
		if (_panel is null) return;
		_panel.Tick(deltaSeconds, time, camera.View, camera.ViewProjection, framebufferSize);
		_panel.Render();

		// Lay the labels out, pin them to where this frame's camera puts their points, then composite and prepare them.
		if (_anchorHost is null || _anchors is null || _anchorPainter is null) return;
		_anchorHost.Tick(deltaSeconds, time, framebufferSize);
		_anchors.Update(camera.ViewProjection, framebufferSize, deltaSeconds);
		_anchorHost.Render();
		_anchorPainter.Prepare();
	}

	// A capture clicks the panel's Increment button once, through the same screen-to-panel projection a real pointer
	// takes. The button's centre on the panel goes to the screen through this frame's camera, so the click lands on it
	// whatever pose the capture holds — the billboard turns the panel with the camera.
	public Vector2? CaptureClick(Camera camera, Vector2 framebufferSize)
	{
		if (_panel is null || _panelView is null)
			return null;
		var button = _panelView.IncrementButton;
		var centre = button.TransformToVisual(null).TransformPoint(new Point(button.LayoutRect.Width / 2f, button.LayoutRect.Height / 2f));
		return _panel.ProjectToScreen(new Vector2(centre.X, centre.Y), camera.ViewProjection, framebufferSize);
	}

	private static readonly SolidColorBrush AnchorAccent = new(HdrColor.FromRgb(120, 180, 255));
	private static readonly SolidColorBrush AnchorBackground = new(HdrColor.FromRgb(16, 20, 28, 220));
	private static readonly SolidColorBrush AnchorText = new(HdrColor.FromRgb(235, 240, 255));

	private static UIElement AnchorDot() => new Ellipse { Width = 8f, Height = 8f, Fill = AnchorAccent };

	private static UIElement AnchorLabel(string text) => new Border
	{
		Background = AnchorBackground,
		BorderBrush = AnchorAccent,
		BorderThickness = new Thickness(1f),
		CornerRadius = new CornerRadius(8f),
		Padding = new Thickness(6f, 2f, 6f, 2f),
		Child = new TextBlock { Text = text, FontSize = 12f, Foreground = AnchorText },
	};
}
