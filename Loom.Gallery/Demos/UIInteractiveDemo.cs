using System.Numerics;

using Loom.Core.Graph;
using Loom.Core.Hosting;
using Loom.Core.Materials;

namespace Loom.Gallery.Demos;

/// <summary>
/// The interactive, data-bound UI proof. The render graph is the same simple lit scene as
/// <see cref="CubeLightingDemo"/>; what's new is host-side — when this demo is active the launcher shows a
/// <c>UIInteractiveView</c> (a XAML page with a <c>Button</c> and an <c>{x:Bind}</c> label) centred in the shared
/// demo shell, driven by the shell's <c>UIHost</c> + pointer input, so clicking the button updates the bound text.
/// The graph itself just declares the scene pass and the shared <see cref="DemoGraph.Hud"/>; the clickable/bound
/// content lives in the host loop (Program.cs) and the page (UI/).
/// </summary>
internal sealed class UIInteractiveDemo : IDemo
{
	private IEngine _engine = null!;
	private ConstantsHandle _viewproj = null!;

	public string Name => "UI Interactive";

	public void DefineTechniques(IEngine engine)
	{
		_engine = engine;
		engine.DefineTechnique("lit", new TechniqueDescriptor("lit", HasVertexInput: true));
	}

	public RenderGraph Build(DemoScene scene) => RenderGraph.Define(g =>
	{
		var screen = g.Backbuffer;
		var depth = g.Screen.Depth("depth", Format.Depth32F);
		_viewproj = g.Constants<Matrix4x4>("uiinteractive.viewproj");

		g.Pass("scene")
			.Reads(_viewproj)
			.Writes(screen)
			.WritesDepth(depth)
			.Draw(ctx =>
			{
				DemoDraw.Mesh(ctx, scene.Cube, "lit", Matrix4x4.Identity);
				DemoDraw.Mesh(ctx, scene.Plane, "lit", Matrix4x4.Identity);
			});

		DemoGraph.Hud(g, screen, scene);
		g.Present(screen);
	});

	// Publish this frame's view-projection for the scene pass to read.
	public void Update(Concepts.Camera camera) => _engine.Write(_viewproj, camera.ViewProjection);
}
