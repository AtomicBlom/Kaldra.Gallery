using Loom.Core;

namespace Loom.Sandbox.Demos;

/// <summary>
/// The bloom sub-graph (Planning.md §7.1) in isolation: render the lit cube to an HDR target, then
/// <c>g.Bloom</c> — a reusable graph fragment (bright-pass → mip-chain downsample → additive upsample →
/// composite) — and blit the result. The HDR key light pushes the lit faces above the bloom threshold so they
/// glow. The whole pyramid's ordering, per-mip barriers, and dead-mip memory aliasing are derived by the engine.
/// </summary>
internal sealed class BloomDemo : IDemo
{
	public string Name => "Bloom";

	public void DefineTechniques(IEngine engine)
	{
		engine.DefineTechnique("lit", new TechniqueDescriptor("lit", HasVertexInput: true));
		// g.Bloom draws with these four (all linear-filtered; the upsample additively accumulates).
		engine.DefineTechnique("bloom.prefilter", new TechniqueDescriptor("bloom_prefilter", LinearFilter: true, HasFragmentPush: true));
		engine.DefineTechnique("bloom.down", new TechniqueDescriptor("bloom_sample", LinearFilter: true));
		engine.DefineTechnique("bloom.up", new TechniqueDescriptor("bloom_sample", Blend: BlendMode.Additive, LinearFilter: true));
		engine.DefineTechnique("bloom.composite", new TechniqueDescriptor("bloom_composite", LinearFilter: true, HasFragmentPush: true));
		engine.DefineTechnique("blit", new TechniqueDescriptor("blit"));
	}

	public RenderGraph Build(DemoScene scene) => RenderGraph.Define(g =>
	{
		var screen = g.Backbuffer;
		var lit = g.Screen.Color("lit", Format.Rgba16F);
		var depth = g.Screen.Depth("depth", Format.Depth32F);

		g.Pass("scene")
			.Writes(lit)
			.WritesDepth(depth)
			.Draw(ctx =>
			{
				ctx.Draw(scene.Cube, "lit");
				ctx.Draw(scene.Plane, "lit");
			});

		var glow = g.Bloom(lit, threshold: 1.0f, mips: 5); // lit faces are HDR (> 1), so they bloom

		g.Pass("blit").Samples(glow).Writes(screen).Draw(ctx => ctx.FullScreen("blit", glow));
		DemoGraph.Hud(g, screen);
		g.Present(screen);
	});
}
