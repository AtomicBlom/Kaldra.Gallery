using System.Numerics;

using Loom.Core.Graph;
using Loom.Core.Hosting;
using Loom.Core.Materials;
using SUIsei;

namespace Loom.Sandbox.Demos;

/// <summary>
/// The interactive, data-bound UI proof. The render graph is the same simple lit scene + overlay UI pass as
/// <see cref="CubeLightingDemo"/>; what's new is host-side — when this demo is active the launcher swaps the overlay
/// surface to a <c>UIInteractiveView</c> (a XAML page with a <c>Button</c> and an <c>{x:Bind}</c> label) driven by a
/// real <c>UIHost</c> + pointer input, so clicking the button updates the bound text. The graph itself just declares
/// the <c>ui</c> pass; the clickable/bound content lives in the host loop (Program.cs) and the page (UI/).
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

		DemoGraph.UI(g, screen, scene.UI, UISurface.Hud);
		DemoGraph.Hud(g, screen);
		g.Present(screen);
	});

	// Publish this frame's view-projection for the scene pass to read.
	public void Update(Concepts.Camera camera) => _engine.Write(_viewproj, camera.ViewProjection);
}
