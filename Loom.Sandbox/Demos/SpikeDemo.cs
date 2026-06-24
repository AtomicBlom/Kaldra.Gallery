using System.Numerics;
using Loom.Core.Geometry;
using Loom.Core.Graph;
using Loom.Core.Hosting;
using Loom.Core.Materials;
using Loom.Concepts;

namespace Loom.Sandbox.Demos;

/// <summary>
/// Spike for the camera/MVP collapse: a cube whose view-projection arrives through a <see cref="ViewConstants"/>
/// buffer the pass <c>.Reads</c> (composed with <see cref="IEngine.ClipSpaceCorrection"/>) and whose model rides
/// the per-draw block via <see cref="DrawSubmitter"/> — not the engine's auto-pushed MVP. The model has translate
/// + rotate + non-uniform scale so the per-draw matrix path is fully exercised. Proves the neutral delivery (and
/// the <c>Loom.Scene</c> helpers) render at Vulkan/DX12 parity before the auto-push is retired engine-wide.
/// </summary>
internal sealed class SpikeDemo : IDemo
{
	public string Name => "Spike (view-proj via constants)";

	private IEngine _engine = null!;
	private ViewConstants _view = null!;
	private readonly DrawSubmitter _submit = new();

	// translate + Y-rotate + non-uniform scale, so a wrong model transpose or normal basis would show.
	private static readonly Matrix4x4 Model =
		Matrix4x4.CreateScale(1.0f, 0.6f, 1.0f)
		* Matrix4x4.CreateRotationY(0.6f)
		* Matrix4x4.CreateTranslation(0.2f, 0f, 0f);

	public void DefineTechniques(IEngine engine)
	{
		_engine = engine;
		engine.DefineTechnique("spike", new TechniqueDescriptor("spike", HasVertexInput: true));
		engine.DefineTechnique("blit", new TechniqueDescriptor("blit"));
	}

	public void Update(Camera camera) => _view.Publish(_engine, camera);

	public RenderGraph Build(DemoScene scene) => RenderGraph.Define(g =>
	{
		var screen = g.Backbuffer;
		_view = new ViewConstants(g, "spike.viewproj");
		var lit = g.Screen.Color("lit", Format.Rgba16F);
		var depth = g.Screen.Depth("depth", Format.Depth32F);

		g.Pass("scene")
			.Reads(_view.Handle)
			.Writes(lit)
			.WritesDepth(depth)
			.Draw(ctx => _submit.Draw(ctx, scene.Cube, "spike", Model));

		g.Pass("blit").Samples(lit).Writes(screen).Draw(ctx => ctx.FullScreen("blit", lit));
		DemoGraph.Hud(g, screen);
		g.Present(screen);
	});
}
