using System.Numerics;

using Loom.Core.Graph;
using Loom.Core.Hosting;
using Loom.Core.Materials;

namespace Loom.Gallery.Demos;

/// <summary>
/// Shadow mapping — the "same draw, a different view" primitive. A depth-only pass renders the casters from the
/// <em>sun's</em> view into a shadow map (the pass reads the sun's view-projection constants); a g-buffer pass
/// renders them from the camera; the lighting pass projects each pixel into the sun's clip space and compares
/// depth to decide shadow. The deferred split is what lets the camera transform (g-buffer) and the sun transform
/// (lighting) each be one published matrix.
/// </summary>
internal sealed class ShadowsDemo : IDemo
{
	private IEngine _engine = null!;
	private Concepts.Camera _sunCam = null!;
	private ConstantsHandle _viewproj = null!;
	private ConstantsHandle _sunViewproj = null!;

	public string Name => "Shadows";

	public void DefineTechniques(IEngine engine)
	{
		_engine = engine;
		_sunCam = DemoSun.View(engine);
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
		_viewproj = g.Constants<Matrix4x4>("shadows.viewproj");
		_sunViewproj = g.Constants<Matrix4x4>("shadows.sun.viewproj");

		// The shadow map lives in its own high-res square domain, rendered from the sun.
		var sun = g.Domain("sun_shadow", 1024, 1024);
		var shadowMap = sun.Depth("sun_depth", Format.Depth32F);

		g.Pass("shadow")
			.Reads(_sunViewproj)
			.WritesDepth(shadowMap)
			.Draw(ctx =>
			{
				DemoDraw.Mesh(ctx, scene.Cube, "depthonly", Matrix4x4.Identity);
				DemoDraw.Mesh(ctx, scene.Plane, "depthonly", Matrix4x4.Identity);
			});

		g.Pass("gbuffer")
			.Reads(_viewproj)
			.Writes(albedo, normal, position)
			.WritesDepth(depth)
			.Draw(ctx =>
			{
				DemoDraw.Mesh(ctx, scene.Cube, "gbuffer", Matrix4x4.Identity);
				DemoDraw.Mesh(ctx, scene.Plane, "gbuffer", Matrix4x4.Identity);
			});

		// Deferred lighting + the shadow test; the sun's view-projection rides the fragment push (the same matrix
		// the shadow pass drew with). Writes the lit, shadowed result straight to the screen.
		g.Pass("lighting")
			.Samples(albedo, normal, position, shadowMap)
			.Writes(screen)
			.Draw(ctx => ctx.FullScreen("light_shadow", DemoGraph.Mat(_sunCam.ViewProjection), albedo, normal, position, shadowMap));

		DemoGraph.Hud(g, screen, scene);
		g.Present(screen);
	});

	// Publish the camera's view-projection (g-buffer pass) and the sun's (shadow depth pass + the lighting shadow test).
	public void Update(Concepts.Camera camera)
	{
		_engine.Write(_viewproj, camera.ViewProjection);
		_engine.Write(_sunViewproj, _sunCam.ViewProjection);
	}
}
