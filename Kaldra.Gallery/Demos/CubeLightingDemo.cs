using System.Numerics;

using BinaryVibrance.Kaldra.Core.Graph;
using BinaryVibrance.Kaldra.Core.Hosting;
using BinaryVibrance.Kaldra.Core.Materials;

namespace Kaldra.Gallery.Demos;

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
				// A non-zero objectId opts the floor out of normal-derived albedo (see lit.slang) — its face-up
				// normal is identical to the cube's top face, so without this the two would render the same colour.
				DemoDraw.Mesh(ctx, scene.Plane, "lit", Matrix4x4.Identity, objectId: 1);
			});

		DemoGraph.Hud(g, screen, scene);
		g.Present(screen);
	});

	// Publish this frame's view-projection for the scene pass to read. The camera
	// already composes the engine's clip-space correction into ViewProjection.
	public void Update(Concepts.Camera camera) => _engine.Write(_viewproj, camera.ViewProjection);
}
