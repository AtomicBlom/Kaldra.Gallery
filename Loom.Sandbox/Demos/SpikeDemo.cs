using System.Numerics;
using System.Runtime.InteropServices;
using Loom.Core.Geometry;
using Loom.Core.Graph;
using Loom.Core.Hosting;
using Loom.Core.Materials;

namespace Loom.Sandbox.Demos;

/// <summary>
/// Spike for the camera/MVP collapse: a cube whose view-projection arrives through a <c>.Reads</c> constants
/// buffer (composed with <see cref="IEngine.ClipSpaceCorrection"/>) and whose model rides the per-draw
/// <c>Uniforms</c> block — not the engine's auto-pushed MVP. The model has translate + rotate + non-uniform
/// scale so the per-draw matrix path is fully exercised. Proves the neutral delivery renders at Vulkan<->DX12
/// parity before the auto-push is retired engine-wide.
/// </summary>
internal sealed class SpikeDemo : IDemo
{
	public string Name => "Spike (view-proj via constants)";

	private IEngine _engine = null!;
	private ConstantsHandle _viewProj = null!;

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

	public void Update(Camera camera)
	{
		// Publish the frame's view-projection as data, composed into the engine's clip space — the app never
		// names a backend NDC convention.
		var vp = camera.ViewProjectionMatrix * _engine.ClipSpaceCorrection;
		_engine.Write(_viewProj, vp);
	}

	public RenderGraph Build(DemoScene scene) => RenderGraph.Define(g =>
	{
		var screen = g.Backbuffer;
		_viewProj = g.Constants<Matrix4x4>("spike.viewproj");
		var lit = g.Screen.Color("lit", Format.Rgba16F);
		var depth = g.Screen.Depth("depth", Format.Depth32F);

		g.Pass("scene")
			.Reads(_viewProj)
			.Writes(lit)
			.WritesDepth(depth)
			.Draw(ctx => ctx.Draw(new DrawCall
			{
				Mesh = scene.Cube,
				Technique = "spike",
				Uniforms = MemoryMarshal.CreateReadOnlySpan(in Model.M11, 16),
			}));

		g.Pass("blit").Samples(lit).Writes(screen).Draw(ctx => ctx.FullScreen("blit", lit));
		DemoGraph.Hud(g, screen);
		g.Present(screen);
	});
}
