using Loom.Core.Graph;
using Loom.Core.Hosting;
using Loom.Core.Materials;
using SUIsei;

namespace Loom.Sandbox.Demos;

/// <summary>
/// MU4 group-opacity proof. The graph is the same lit scene + overlay <c>ui</c> pass as the other UI demos; what it
/// exercises is the host-side overlay content (built in Program.cs): a faded container holding overlapping
/// translucent children. True group opacity renders the whole subtree once at full strength and fades the result a
/// single time — so the overlap region stays the same colour as the non-overlap. The backend proves this by rendering
/// the faded subtree into a pooled offscreen target and compositing it back at the group alpha (the GPU twin of the
/// software rasterizer's layer stack); a per-child fade would visibly darken the overlap. This is the cross-backend
/// parity gate — the only UI path whose pixels depend on the backend.
/// </summary>
internal sealed class UIGroupOpacityDemo : IDemo
{
	public string Name => "UI Group Opacity";

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
				ctx.Draw(DrawCall.Create(scene.Cube, "lit"));
				ctx.Draw(DrawCall.Create(scene.Plane, "lit"));
			});

		DemoGraph.UI(g, screen, scene.UI, UISurface.Hud);
		DemoGraph.Hud(g, screen);
		g.Present(screen);
	});
}
