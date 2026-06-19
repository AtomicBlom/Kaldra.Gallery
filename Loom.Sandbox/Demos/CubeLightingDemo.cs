using Loom.Core;

namespace Loom.Sandbox.Demos;

/// <summary>
/// The simplest meaningful pipeline: draw the cube and ground plane with a single forward directional light,
/// straight to the screen. One geometry pass, one shader (<c>lit</c>), a depth buffer. The clearest starting
/// point for reading how geometry + a technique + a depth target become a frame.
/// </summary>
internal sealed class CubeLightingDemo : IDemo
{
	public string Name => "Cube + Lighting";

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

		// MU0 Phase A: the game-UI overlay seam (no-op renderer). Proves the "ui" pass schedules + DrawUI records
		// clean on both backends; under the debug HUD so ImGui stays on top.
		DemoGraph.Ui(g, screen, UiSurface.Hud);
		DemoGraph.Hud(g, screen);
		g.Present(screen);
	});
}
