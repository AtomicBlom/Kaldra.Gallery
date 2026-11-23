using System.Numerics;

using BinaryVibrance.Kaldra.Core.Graph;
using BinaryVibrance.Kaldra.Core.Hosting;
using BinaryVibrance.Kaldra.Core.Materials;

namespace Kaldra.Gallery.Demos;

/// <summary>
/// The bloom sub-graph in isolation. The cube is drawn <em>emissive</em> — a bright HDR
/// source (~5×) — over a dim lit floor, so it visibly glows; <c>g.Bloom</c> (a reusable graph fragment:
/// bright-pass → mip-chain downsample → additive upsample → composite) spreads a halo from the bright pixels
/// and the floor (below threshold) does not. The whole pyramid's ordering, per-mip barriers, and dead-mip
/// memory aliasing are derived by the engine.
/// </summary>
internal sealed class BloomDemo : IDemo
{
	private IEngine _engine = null!;
	private ConstantsHandle _viewproj = null!;

	public string Name => "Bloom";

	public void DefineTechniques(IEngine engine)
	{
		_engine = engine;
		engine.DefineTechnique("emissive", new TechniqueDescriptor("emissive", HasVertexInput: true)); // the glowing cube
		engine.DefineTechnique("lit", new TechniqueDescriptor("lit", HasVertexInput: true));           // the dim floor
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
		var hdr = g.Screen.Color("hdr", Format.Rgba16F);
		var depth = g.Screen.Depth("depth", Format.Depth32F);
		_viewproj = g.Constants<Matrix4x4>("bloom.viewproj");

		g.Pass("scene")
			.Reads(_viewproj)
			.Writes(hdr)
			.WritesDepth(depth)
			.Draw(ctx =>
			{
				DemoDraw.Mesh(ctx, scene.Plane, "lit", Matrix4x4.Identity);       // a dim floor for context (below the bloom threshold)
				DemoDraw.Mesh(ctx, scene.Cube, "emissive", Matrix4x4.Identity);   // the bright, glowing object
			});

		var glow = g.Bloom(hdr, threshold: 1.0f, mips: 5, intensity: 1.0f);

		g.Pass("blit").Samples(glow).Writes(screen).Draw(ctx => ctx.FullScreen("blit", glow));
		DemoGraph.Hud(g, screen, scene);
		g.Present(screen);
	});

	// Publish this frame's view-projection for the scene pass to read.
	public void Update(Concepts.Camera camera) => _engine.Write(_viewproj, camera.ViewProjection);
}
