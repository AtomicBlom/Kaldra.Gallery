using System.Numerics;

using Loom.Concepts;
using Loom.Core.Geometry;
using Loom.Core.Graph;
using Loom.Core.Hosting;
using Loom.Core.Materials;
#if LOOM_WEB
using Loom.Web.UI;
#else
using Loom.Sandbox.UI;
#endif
using Loom.UI.SUIsei;
using SUIsei;
using SUIsei.Input;

namespace Loom.Sandbox.Demos;

/// <summary>
/// Demonstrates in-world UI placement: a SUIsei panel rendered into an offscreen texture and mapped onto a
/// world-space quad, with screen-space pointer events projected onto the quad's surface so clicks reach the
/// in-world buttons. The panel is an independent <see cref="InWorldPanel"/> whose dedicated <c>UIHost</c>
/// never sees the 3D world — all projection logic lives in the bridge.
/// </summary>
internal sealed class InWorldUIDemo : IDemo
{
	private InWorldPanel? _panel;
	private InWorldPanelView? _panelView;
	private Mesh? _panelQuad;
	private ConstantsHandle _viewproj = null!;
	private IEngine _engine = null!;

	public string Name => "In-World UI";

	public void DefineTechniques(IEngine engine)
	{
		_engine = engine;
		engine.DefineTechnique("lit", new TechniqueDescriptor("lit", HasVertexInput: true));
		engine.DefineTechnique("ui_in_world", new TechniqueDescriptor("ui_in_world",
			HasVertexInput: true, LinearFilter: true, Blend: BlendMode.PremultipliedAlpha));

		// A centered unit quad in the XY plane (±0.5), facing +Z. Vertices span [-0.5, 0.5]² so the
		// WorldTransform's scale directly sets the panel's physical world dimensions. UV covers [0,1]²
		// mapping (0,0)=top-left, (1,1)=bottom-right to match SUIsei's compositor output.
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
			worldTransform: worldTransform, billboard: true, fontAtlas: FontAtlas.Default);
		_panelView = new InWorldPanelView();
		_panel.View.Root.Content = _panelView;
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

		// 1. Composite the SUIsei tree into the offscreen panel texture.
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
					call.SetUniforms(block);
					call.Sampled = new ResourceHandle[] { panelTex };
					ctx.Draw(call);
				}
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
	}
}
