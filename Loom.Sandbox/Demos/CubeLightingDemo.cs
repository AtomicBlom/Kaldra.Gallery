using System.Numerics;

using Loom.Core.Graph;
using Loom.Core.Hosting;
using Loom.Core.Materials;
using SUIsei;

namespace Loom.Sandbox.Demos;

/// <summary>
/// The simplest meaningful pipeline: draw the cube and ground plane with a single forward directional light,
/// straight to the screen. One geometry pass, one shader (<c>lit</c>), a depth buffer. The clearest starting
/// point for reading how geometry + a technique + a depth target become a frame.
/// </summary>
internal sealed class CubeLightingDemo : IDemo
{
	private IEngine _engine = null!;
	private ConstantsHandle _viewproj = null!;

	public string Name => "Cube + Lighting";

	public void DefineTechniques(IEngine engine)
	{
		_engine = engine;
		engine.DefineTechnique("lit", new TechniqueDescriptor("lit", HasVertexInput: true));
	}

	public RenderGraph Build(DemoScene scene) => RenderGraph.Define(g =>
	{
		var screen = g.Backbuffer;
		var depth = g.Screen.Depth("depth", Format.Depth32F);
		_viewproj = g.Constants<Matrix4x4>("cubelighting.viewproj");

		g.Pass("scene")
			.Reads(_viewproj)
			.Writes(screen)
			.WritesDepth(depth)
			.Draw(ctx =>
			{
				DemoDraw.Mesh(ctx, scene.Cube, "lit", Matrix4x4.Identity);
				DemoDraw.Mesh(ctx, scene.Plane, "lit", Matrix4x4.Identity);
			});

		// The game-UI overlay: the painter submits the composited HUD list through the public draw-list API, under
		// the debug HUD so ImGui stays on top.
		DemoGraph.UI(g, screen, scene.UI, UISurface.Hud);
		DemoGraph.Hud(g, screen);
		g.Present(screen);
	});

	// Publish this frame's view-projection for the scene pass to read (the engine no longer pushes it). The camera
	// already composes the engine's clip-space correction into ViewProjection.
	public void Update(Concepts.Camera camera) => _engine.Write(_viewproj, camera.ViewProjection);
}
