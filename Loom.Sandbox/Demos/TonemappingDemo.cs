using Loom.Core;
using Loom.Core.Graph;
using Loom.Core.Hosting;
using Loom.Core.Materials;

namespace Loom.Sandbox.Demos;

/// <summary>
/// Auto-exposure + tone-mapping (Planning.md §12 M5). The lit scene is HDR; a compute pass meters its average
/// luminance and read-writes a 1×1 <em>persistent</em> exposure value (it carries across frames, so the
/// adaptation is temporal); the tone-map then scales by that exposure and rolls the highlights off to the
/// display. Shows the GPU→persistent→GPU loop and the compute→fragment hand-off the engine derives. (When HDR
/// output is on the tone-map keeps the extended range instead — see <c>ctx.HdrOutput</c>.)
/// </summary>
internal sealed class TonemappingDemo : IDemo
{
	public string Name => "Tonemapping";

	public void DefineTechniques(IEngine engine)
	{
		engine.DefineTechnique("lit", new TechniqueDescriptor("lit", HasVertexInput: true));
		engine.DefineTechnique("exposure", new TechniqueDescriptor("exposure", VertexEntry: "main", FragmentEntry: "main", IsCompute: true, HasFragmentPush: true));
		engine.DefineTechnique("tonemap", new TechniqueDescriptor("tonemap", HasFragmentPush: true));
		engine.DefineTechnique("blit", new TechniqueDescriptor("blit"));
	}

	public RenderGraph Build(DemoScene scene) => RenderGraph.Define(g =>
	{
		var screen = g.Backbuffer;
		var lit = g.Screen.Color("lit", Format.Rgba16F);
		var depth = g.Screen.Depth("depth", Format.Depth32F);
		var meter = g.Domain("meter", 1, 1);
		var exposure = meter.Persistent("exposure", Format.R16F);
		var graded = g.Screen.Color("graded", Format.Rgba16F);

		g.Pass("scene")
			.Writes(lit)
			.WritesDepth(depth)
			.Draw(ctx =>
			{
				ctx.Draw(scene.Cube, "lit");
				ctx.Draw(scene.Plane, "lit");
			});

		// Meter the lit scene's luminance into the 1×1 persistent exposure (the rate rides the push).
		g.Compute("exposure")
			.Samples(lit)
			.ReadWrites(exposure)
			.Dispatch(ctx => ctx.Dispatch("exposure", (1, 1, 1), [8f * ctx.DeltaTime], [lit], [exposure]));

		// Scale by exposure + roll off highlights (SDR), or preserve the extended range (HDR output).
		g.Pass("tonemap")
			.Samples(lit, exposure)
			.Writes(graded)
			.Draw(ctx => ctx.FullScreen("tonemap", [ctx.HdrOutput ? 1f : 0f], lit, exposure));

		g.Pass("blit").Samples(graded).Writes(screen).Draw(ctx => ctx.FullScreen("blit", graded));
		DemoGraph.Hud(g, screen);
		g.Present(screen);
	});
}
