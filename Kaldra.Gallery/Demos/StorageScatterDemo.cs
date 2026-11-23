using System.Numerics;

using BinaryVibrance.Kaldra.Core.Graph;
using BinaryVibrance.Kaldra.Core.Hosting;
using BinaryVibrance.Kaldra.Core.Materials;

namespace Kaldra.Gallery.Demos;

/// <summary>
/// A compute pass reading app-written storage: a density map written once into a <c>g.Storage</c> buffer (a ring) that
/// a compute pass <c>.Reads</c>, as a draw reads one, and expands every frame into a field of sparks: a grid of cells,
/// each kept where a hash of its coordinates falls under the map's density there, written into a buffer the draw reads
/// as instances (as the Particles demo's are). The shape of a game scattering grass from a map of its world around the
/// eye. The map's write is uploaded only into the frames in flight behind it, so a map written once costs nothing per
/// frame.
/// </summary>
/// <param name="filled">Write a map of density 1 everywhere rather than the ring: the control a test holds the ring to,
/// since a dispatch reading the wrong buffer, or none, would scatter the same field either way.</param>
internal sealed class StorageScatterDemo(bool filled = false) : IDemo
{
	private const int Cells = 96, Map = 16;

	private IEngine _engine = null!;
	private ConstantsHandle _viewproj = null!;
	private StorageBufferHandle _density = null!;
	private bool _densityWritten;   // once per graph: the buffer keeps its last write

	public string Name => "Storage Scatter";

	public void DefineTechniques(IEngine engine)
	{
		_engine = engine;
		engine.DefineTechnique("lit", new TechniqueDescriptor("lit", HasVertexInput: true));
		engine.DefineTechnique("scatter.expand", TechniqueDescriptor.Compute("storage_scatter", hasFragmentPush: true));
		engine.DefineTechnique("scatter.render", new TechniqueDescriptor("particle_render", Blend: BlendMode.Additive, Instance: SandboxVertexLayouts.ParticlePosition));
		engine.DefineTechnique("blit", new TechniqueDescriptor("blit"));
	}

	public RenderGraph Build(DemoScene scene) => RenderGraph.Define(g =>
	{
		var screen = g.Backbuffer;
		var lit = g.Screen.Color("lit", Format.Rgba16F);
		var depth = g.Screen.Depth("depth", Format.Depth32F);

		var sparks = g.Buffer("scatter.sparks", Cells * Cells, SandboxVertexLayouts.ParticlePosition);
		_viewproj = g.Constants<Matrix4x4>("scatter.viewproj");
		_density = g.Storage<float>("scatter.density", Map * Map);
		_densityWritten = false;

		g.Pass("scene")
			.Reads(_viewproj)
			.Writes(lit)
			.WritesDepth(depth)
			.Draw(ctx => DemoDraw.Mesh(ctx, scene.Plane, "lit", Matrix4x4.Identity));

		g.Compute("scatter.expand")
			.Reads(_density)
			.ReadWrites(sparks)
			.Dispatch(ctx => ctx.Dispatch("scatter.expand", ((uint)(Cells * Cells + 63) / 64, 1, 1), [(float)ctx.Time], [], [sparks]));

		g.Pass("sparks")
			.Reads(_viewproj)
			.Blends(lit)
			.ReadsVertex(sparks)
			.Draw(ctx => ctx.Draw(new DrawCall { Technique = "scatter.render", VertexCount = 6, Instances = DrawCall.Gpu(sparks, Cells * Cells) }));

		g.Pass("blit").Samples(lit).Writes(screen).Draw(ctx => ctx.FullScreen("blit", lit));
		DemoGraph.Hud(g, screen, scene);
		g.Present(screen);
	});

	// This frame's view-projection, and the map once, the first frame of its graph
	public void Update(Concepts.Camera camera)
	{
		_engine.Write(_viewproj, camera.ViewProjection);
		if (_densityWritten) return;
		var map = new float[Map * Map];
		for (int z = 0; z < Map; z++)
			for (int x = 0; x < Map; x++)
			{
				float dx = (x + 0.5f) / Map - 0.5f, dz = (z + 0.5f) / Map - 0.5f;
				float r = MathF.Sqrt(dx * dx + dz * dz);
				map[x + z * Map] = filled ? 1f : MathF.Max(0f, 1f - MathF.Abs(r - 0.3f) / 0.12f);   // a ring, or the control's whole field
			}
		_engine.Write(_density, map);
		_densityWritten = true;
	}
}
