using System.Numerics;
using Loom.Core.Graph;
using Loom.Core.Hosting;
using Loom.Core.Materials;

namespace Loom.Sandbox.Demos;

/// <summary>
/// A cube whose view-projection arrives through a <see cref="GraphBuilder.Constants{T}"/> buffer the pass
/// <c>.Reads</c> (composed with <see cref="IEngine.ClipSpaceCorrection"/>) and whose model rides the per-draw
/// block via <see cref="DemoDraw.Mesh"/>. The model has translate + rotate + non-uniform scale so the per-draw
/// matrix path is fully exercised. Verifies that the neutral constants delivery renders at Vulkan/DX12 parity.
/// </summary>
internal sealed class SpikeDemo : IDemo
{
	public string Name => "Spike (view-proj via constants)";

	private IEngine _engine = null!;
	private ConstantsHandle _viewproj = null!;

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

	public void Update(Concepts.Camera camera) => _engine.Write(_viewproj, camera.ViewProjection);

	public RenderGraph Build(DemoScene scene) => RenderGraph.Define(g =>
	{
		var screen = g.Backbuffer;
		_viewproj = g.Constants<Matrix4x4>("spike.viewproj");
		var lit = g.Screen.Color("lit", Format.Rgba16F);
		var depth = g.Screen.Depth("depth", Format.Depth32F);

		g.Pass("scene")
			.Reads(_viewproj)
			.Writes(lit)
			.WritesDepth(depth)
			.Draw(ctx => DemoDraw.Mesh(ctx, scene.Cube, "spike", Model));

		g.Pass("blit").Samples(lit).Writes(screen).Draw(ctx => ctx.FullScreen("blit", lit));
		DemoGraph.Hud(g, screen);
		g.Present(screen);
	});
}
