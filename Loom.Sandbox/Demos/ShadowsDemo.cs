using Loom.Core.Graph;
using Loom.Core.Hosting;
using Loom.Core.Materials;

namespace Loom.Sandbox.Demos;

/// <summary>
/// Shadow mapping (Planning.md §7.2) — the novel "same draw, a different view" primitive. A depth-only pass
/// renders the casters from the <em>sun's</em> view into a shadow map (<c>ctx.Draw(mesh, "depthonly",
/// ctx.Views[ViewKeys.Sun])</c>); a g-buffer pass renders them from the camera; the lighting pass projects each pixel into
/// the sun's clip space and compares depth to decide shadow. The deferred split is what lets the camera
/// transform (g-buffer vertex push) and the sun transform (lighting fragment push) each be one matrix.
/// </summary>
internal sealed class ShadowsDemo : IDemo
{
	public string Name => "Shadows";

	public void DefineTechniques(IEngine engine)
	{
		engine.DefineTechnique("depthonly", new TechniqueDescriptor("depthonly", HasVertexInput: true));
		engine.DefineTechnique("gbuffer", new TechniqueDescriptor("gbuffer", HasVertexInput: true));
		engine.DefineTechnique("light_shadow", new TechniqueDescriptor("light_shadow", HasFragmentPush: true));
	}

	public RenderGraph Build(DemoScene scene) => RenderGraph.Define(g =>
	{
		var screen = g.Backbuffer;
		var albedo = g.Screen.Color("albedo", Format.Rgba8Srgb);
		var normal = g.Screen.Color("normal", Format.Rgba16F);
		var position = g.Screen.Color("position", Format.Rgba16F);
		var depth = g.Screen.Depth("depth", Format.Depth32F);

		// The shadow map lives in its own high-res square domain, rendered from the sun.
		var sun = g.Domain("sun_shadow", 1024, 1024);
		var shadowMap = sun.Depth("sun_depth", Format.Depth32F);

		g.Pass("shadow")
			.WritesDepth(shadowMap)
			.Draw(ctx =>
			{
				ctx.Draw(new DrawCall { Mesh = scene.Cube, Technique = "depthonly", View = ctx.Views[ViewKeys.Sun] });
				ctx.Draw(new DrawCall { Mesh = scene.Plane, Technique = "depthonly", View = ctx.Views[ViewKeys.Sun] });
			});

		g.Pass("gbuffer")
			.Writes(albedo, normal, position)
			.WritesDepth(depth)
			.Draw(ctx =>
			{
				ctx.Draw(DrawCall.Create(scene.Cube, "gbuffer"));
				ctx.Draw(DrawCall.Create(scene.Plane, "gbuffer"));
			});

		// Deferred lighting + the shadow test; the sun's view-projection rides the fragment push (the same
		// matrix the shadow pass drew with). Writes the lit, shadowed result straight to the screen.
		g.Pass("lighting")
			.Samples(albedo, normal, position, shadowMap)
			.Writes(screen)
			.Draw(ctx => ctx.FullScreen("light_shadow", DemoGraph.Mat(ctx.Views[ViewKeys.Sun].ViewProjectionMatrix), albedo, normal, position, shadowMap));

		DemoGraph.Hud(g, screen);
		g.Present(screen);
	});
}
