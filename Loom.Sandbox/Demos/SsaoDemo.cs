using System.Numerics;

using Loom.Concepts;
using Loom.Core.Geometry;
using Loom.Core.Graph;
using Loom.Core.Hosting;
using Loom.Core.Materials;

namespace Loom.Sandbox.Demos;

/// <summary>
/// Screen-space ambient occlusion. A g-buffer pass writes world position + normal; the
/// <c>ssao</c> pass samples them and writes a per-pixel occlusion term; <c>ssao.blur</c> smooths its rotation
/// noise; <c>ssao_view</c> shows the albedo darkened by that term, so the contact shadows (where the cube meets
/// the floor, in concave corners) read directly. Adding SSAO is just adding edges — gbuffer → ssao → ssao.blur →
/// view — and the engine derives the ordering.
/// </summary>
internal sealed class SsaoDemo : IDemo
{
	private IEngine _engine = null!;
	private ViewConstants _view = null!;
	private readonly DrawSubmitter _submit = new();

	public string Name => "SSAO";

	public void DefineTechniques(IEngine engine)
	{
		_engine = engine;
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
		_view = new ViewConstants(g, "ssao.viewproj");

		g.Pass("gbuffer")
			.Reads(_view.Handle)
			.Writes(albedo, normal, position)
			.WritesDepth(depth)
			.Draw(ctx =>
			{
				_submit.Draw(ctx, scene.Cube, "gbuffer", Matrix4x4.Identity);
				_submit.Draw(ctx, scene.Plane, "gbuffer", Matrix4x4.Identity);
			});

		g.Pass("ssao.calculate")
			.Samples(position, normal)
			.Writes(ao)
			.Draw(ctx => ctx.FullScreen("ssao", DemoGraph.Mat(ctx.View.ViewProjectionMatrix), position, normal));

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

	// Publish this frame's camera view-projection for the g-buffer pass to read.
	public void Update(Camera camera) => _view.Publish(_engine, camera);
}
