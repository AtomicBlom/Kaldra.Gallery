using Loom.Core.Graph;
using Loom.Core.Hosting;
using Loom.Core.Materials;

namespace Loom.Sandbox.Demos;

/// <summary>
/// Normal-mapped deferred lighting (PlanningPhase2.md M11). The same deferred path as <see cref="ShadowsDemo"/>
/// — shadow map → G-buffer → lighting — but the geometry is drawn with a <em>material</em>: the
/// <c>gbuffer.nm</c> technique samples a brick albedo + tangent-space normal map and writes the perturbed world
/// normal into the same G-buffer the existing lighting pass reads, so the relief lights and shadows correctly
/// with no change to the lighting shader. The grazing sun makes the per-texel normal detail read as raised
/// brick. The brick material (and its mip chain) is loaded from committed PNGs by the host and handed in via
/// <see cref="DemoScene.NormalMapped"/>.
/// </summary>
internal sealed class NormalMappingDemo : IDemo
{
	public string Name => "Normal mapping";

	public void DefineTechniques(IEngine engine)
	{
		engine.DefineTechnique("depthonly", new TechniqueDescriptor("depthonly", HasVertexInput: true));
		// The normal-mapped G-buffer technique: reads the mesh + two material textures (slots 0/1), Repeat-tiled.
		engine.DefineTechnique("gbuffer.nm", new TechniqueDescriptor("gbuffer_normalmap", HasVertexInput: true, LinearFilter: true, Address: SamplerAddress.Repeat));
		engine.DefineTechnique("light_shadow", new TechniqueDescriptor("light_shadow", HasFragmentPush: true));
	}

	public RenderGraph Build(DemoScene scene) => RenderGraph.Define(g =>
	{
		var screen = g.Backbuffer;
		var albedo = g.Screen.Color("albedo", Format.Rgba8Srgb);
		var normal = g.Screen.Color("normal", Format.Rgba16F);
		var position = g.Screen.Color("position", Format.Rgba16F);
		var depth = g.Screen.Depth("depth", Format.Depth32F);

		var sun = g.Domain("sun_shadow", 1024, 1024);
		var shadowMap = sun.Depth("sun_depth", Format.Depth32F);

		g.Pass("shadow")
			.WritesDepth(shadowMap)
			.Draw(ctx =>
			{
				ctx.Draw(new DrawCall { Mesh = scene.Cube, Technique = "depthonly", View = ctx.Views[ViewKeys.Sun] });
				ctx.Draw(new DrawCall { Mesh = scene.Plane, Technique = "depthonly", View = ctx.Views[ViewKeys.Sun] });
			});

		// Deferred geometry, drawn with the brick material — the only difference from the plain deferred path.
		g.Pass("gbuffer")
			.Writes(albedo, normal, position)
			.WritesDepth(depth)
			.Draw(ctx =>
			{
				ctx.Draw(new DrawCall { Mesh = scene.Cube, Material = scene.NormalMapped, Technique = "gbuffer.nm" });
				ctx.Draw(new DrawCall { Mesh = scene.Plane, Material = scene.NormalMapped, Technique = "gbuffer.nm" });
			});

		g.Pass("lighting")
			.Samples(albedo, normal, position, shadowMap)
			.Writes(screen)
			.Draw(ctx => ctx.FullScreen("light_shadow", DemoGraph.Mat(ctx.Views[ViewKeys.Sun].ViewProjectionMatrix), albedo, normal, position, shadowMap));

		DemoGraph.Hud(g, screen);
		g.Present(screen);
	});
}
