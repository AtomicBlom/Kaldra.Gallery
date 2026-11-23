using System.Numerics;

using BinaryVibrance.Kaldra.Core.Graph;
using BinaryVibrance.Kaldra.Core.Hosting;
using BinaryVibrance.Kaldra.Core.Materials;

namespace Kaldra.Gallery.Demos;

/// <summary>
/// Multisampling, and the resolve it derives. The cube and its floor, and a grille of thin bars standing behind them,
/// are drawn into a domain of four samples a pixel; the blit pass then samples that target, which is where the engine
/// resolves it into a single-sample image for the blit to read. Nothing in the graph says "resolve": a pass sampling a
/// multisampled target is enough. The grille's bars are a cut-out drawn by alpha-to-coverage
/// (<see cref="TechniqueDescriptor.AlphaToCoverage"/>), so their edges are as smooth as the cube's.
/// </summary>
/// <param name="samples">Samples a pixel: four in the gallery; one for a test's control render.</param>
/// <param name="grille">Whether to draw the grille. How alpha becomes a sample mask is the driver's choice, and differs
/// between APIs on the same GPU, so the cross-backend parity tests leave it out and compare the resolve alone.</param>
internal sealed class MsaaDemo(int samples = 4, bool grille = true) : IDemo
{
	private IEngine _engine = null!;
	private ConstantsHandle _viewproj = null!;

	public string Name => "MSAA";

	public void DefineTechniques(IEngine engine)
	{
		_engine = engine;
		engine.DefineTechnique("lit", new TechniqueDescriptor("lit", HasVertexInput: true));
		engine.DefineTechnique("cutout", new TechniqueDescriptor("cutout", HasVertexInput: true, AlphaToCoverage: true));
		engine.DefineTechnique("blit", new TechniqueDescriptor("blit"));
	}

	public RenderGraph Build(DemoScene scene) => RenderGraph.Define(g =>
	{
		var screen = g.Backbuffer;
		var msaa = g.Domain("msaa", 1f, samples: samples);   // the screen's size, four samples a pixel
		var color = msaa.Color("color", Format.Rgba16F, clearValue: new Vector4(0.06f, 0.07f, 0.09f, 1f));
		var depth = msaa.Depth("depth", Format.Depth32F);
		_viewproj = g.Constants<Matrix4x4>("msaa.viewproj");

		g.Pass("scene")
			.Reads(_viewproj)
			.Writes(color)
			.WritesDepth(depth)
			.Draw(ctx =>
			{
				DemoDraw.Mesh(ctx, scene.Cube, "lit", Matrix4x4.Identity);
				DemoDraw.Mesh(ctx, scene.Plane, "lit", Matrix4x4.Identity, objectId: 1);
				if (!grille)
					return;
				// The grille: the floor's quad, shrunk and stood up behind the cube, facing it
				var bars = Matrix4x4.CreateScale(0.2f) * Matrix4x4.CreateRotationX(MathF.PI / 2f) * Matrix4x4.CreateTranslation(0.6f, 0.9f, -1.2f);
				DemoDraw.Mesh(ctx, scene.Plane, "cutout", bars);
			});

		// Sampling the multisampled target is what resolves it
		g.Pass("blit").Samples(color).Writes(screen).Draw(ctx => ctx.FullScreen("blit", color));
		DemoGraph.Hud(g, screen, scene);
		g.Present(screen);
	});

	public void Update(Concepts.Camera camera) => _engine.Write(_viewproj, camera.ViewProjection);
}
