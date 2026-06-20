using Loom.Core;
using Loom.Core.Graph;
using Loom.Core.Hosting;
using Loom.Core.Materials;
using Loom.Core.UI;

namespace Loom.Sandbox.Demos;

/// <summary>
/// MU2: the interactive, data-bound UI proof. The render graph is the same simple lit scene + overlay UI pass as
/// <see cref="CubeLightingDemo"/>; what's new is host-side — when this demo is active the launcher swaps the overlay
/// surface to a <c>UIInteractiveView</c> (a XAML page with a <c>Button</c> and an <c>{x:Bind}</c> label) driven by a
/// real <c>UIHost</c> + pointer input, so clicking the button updates the bound text. The graph itself just declares
/// the <c>ui</c> pass; the clickable/bound content lives in the host loop (Program.cs) and the page (UI/).
/// </summary>
internal sealed class UIInteractiveDemo : IDemo
{
	public string Name => "UI Interactive";

	public void DefineTechniques(IEngine engine) =>
		engine.DefineTechnique("lit", new TechniqueDescriptor("lit", HasVertexInput: true));

	public RenderGraph Build(DemoScene scene) => RenderGraph.Define(g =>
	{
		var screen = g.Backbuffer;
		var depth = g.Screen.Depth("depth", Format.Depth32F);

		g.Pass("scene")
			.Writes(screen)
			.WritesDepth(depth)
			.Draw(ctx =>
			{
				ctx.Draw(scene.Cube, "lit");
				ctx.Draw(scene.Plane, "lit");
			});

		DemoGraph.UI(g, screen, UISurface.Hud);
		DemoGraph.Hud(g, screen);
		g.Present(screen);
	});
}
