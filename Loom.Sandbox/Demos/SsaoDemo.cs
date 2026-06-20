using Loom.Core.Graph;
using Loom.Core.Hosting;
using Loom.Core.Materials;

namespace Loom.Sandbox.Demos;

/// <summary>
/// Screen-space ambient occlusion (Planning.md §7.6). A g-buffer pass writes world position + normal; the
/// <c>ssao</c> pass samples them and writes a per-pixel occlusion term; <c>ssao.blur</c> smooths its rotation
/// noise; <c>ssao_view</c> shows the albedo darkened by that term, so the contact shadows (where the cube meets
/// the floor, in concave corners) read directly. Adding SSAO is just adding edges — gbuffer → ssao → ssao.blur →
/// view — and the engine derives the ordering.
/// </summary>
internal sealed class SsaoDemo : IDemo
{
	public string Name => "SSAO";

	public void DefineTechniques(IEngine engine)
	{
		engine.DefineTechnique("gbuffer", new TechniqueDescriptor("gbuffer", HasVertexInput: true));
		engine.DefineTechnique("ssao", new TechniqueDescriptor("ssao", HasFragmentPush: true)); // camera VP rides the push
		engine.DefineTechnique("ssao.blur", new TechniqueDescriptor("ssao_blur"));
		engine.DefineTechnique("ssao_view", new TechniqueDescriptor("ssao_view"));
	}

	public RenderGraph Build(DemoScene scene) => RenderGraph.Define(g =>
	{
		var screen = g.Backbuffer;
		var albedo = g.Screen.Color("albedo", Format.Rgba8Srgb);
		var normal = g.Screen.Color("normal", Format.Rgba16F);
		var position = g.Screen.Color("position", Format.Rgba16F);
		var depth = g.Screen.Depth("depth", Format.Depth32F);
		var ao = g.Screen.Color("ao", Format.R16F);
		var aoBlur = g.Screen.Color("ao_blur", Format.R16F);

		g.Pass("gbuffer")
			.Writes(albedo, normal, position)
			.WritesDepth(depth)
			.Draw(ctx =>
			{
				ctx.Draw(scene.Cube, "gbuffer");
				ctx.Draw(scene.Plane, "gbuffer");
			});

		g.Pass("ssao.calculate")
			.Samples(position, normal)
			.Writes(ao)
			.Draw(ctx => ctx.FullScreen("ssao", DemoGraph.Mat(ctx.Camera.ViewProjection), position, normal));

		g.Pass("ssao.blur")
			.Samples(ao)
			.Writes(aoBlur)
			.Draw(ctx => ctx.FullScreen("ssao.blur", ao));

		g.Pass("view")
			.Samples(albedo, aoBlur)
			.Writes(screen)
			.Draw(ctx => ctx.FullScreen("ssao_view", albedo, aoBlur));

		DemoGraph.Hud(g, screen);
		g.Present(screen);
	});
}
