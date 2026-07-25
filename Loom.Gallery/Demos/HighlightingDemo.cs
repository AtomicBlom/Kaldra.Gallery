using System.Numerics;

using Loom.Core.Graph;
using Loom.Core.Hosting;
using Loom.Core.Materials;

namespace Loom.Gallery.Demos;

/// <summary>
/// Object highlighting (picking + jump-flood outline). The cube writes its object id; the id buffer is read
/// back to the CPU so the host knows which object the cursor is over; a seed pass marks the hovered object's
/// pixels and a jump-flood ping-pong (steps 4→2→1) propagates the nearest seed; resolve turns nearest-seed
/// distance into an emissive ring composited over the lit scene. The (read, write) handle swap is the ping-pong
/// idiom — two physical buffers, swapped in a normal loop, and the graph follows the handles. Runs in a fixed
/// 320×180 domain so the flood has concrete pixel dimensions and the cursor maps cleanly.
/// </summary>
internal sealed class HighlightingDemo : IDemo
{
	private ColorHandle _ids = null!;
	private ConstantsHandle _hoverParams = null!;
	private IEngine _engine = null!;
	private ConstantsHandle _viewproj = null!;

	public string Name => "Object Highlighting";

	public void DefineTechniques(IEngine engine)
	{
		_engine = engine;
		engine.DefineTechnique("lit", new TechniqueDescriptor("lit", HasVertexInput: true));
		engine.DefineTechnique("id", new TechniqueDescriptor("id", HasVertexInput: true));
		engine.DefineTechnique("outline.seed", new TechniqueDescriptor("outline_seed"));
		engine.DefineTechnique("outline.jfa", new TechniqueDescriptor("outline_jfa", HasFragmentPush: true));
		engine.DefineTechnique("outline.resolve", new TechniqueDescriptor("outline_resolve"));
		engine.DefineTechnique("blit", new TechniqueDescriptor("blit"));
	}

	public RenderGraph Build(DemoScene scene) => RenderGraph.Define(g =>
	{
		var screen = g.Backbuffer;
		var world = g.Domain("world", 320, 180);
		var lit = world.Color("lit", Format.Rgba16F);
		var depth = world.Depth("depth", Format.Depth32F);
		var ids = world.Color("object_id", Format.R16F);
		var seedA = world.Color("outline_a", Format.Rg16F);
		var seedB = world.Color("outline_b", Format.Rg16F);
		var outlined = world.Color("lit_outlined", Format.Rgba16F);
		_viewproj = g.Constants<Matrix4x4>("highlighting.viewproj");

		g.Pass("scene")
			.Reads(_viewproj)
			.Writes(lit)
			.WritesDepth(depth)
			.Draw(ctx =>
			{
				DemoDraw.Mesh(ctx, scene.Cube, "lit", Matrix4x4.Identity);
				DemoDraw.Mesh(ctx, scene.Plane, "lit", Matrix4x4.Identity);
			});

		// Write the cube's object id (1), then read it back so the host can decide the hovered object. The host
		// hands the hovered id to the seed pass through this parameter buffer (WriteHover) — no engine-side picking
		// state, just the general readback + parameter-buffer primitives. The id rides the per-draw block now, so
		// the id shader writes it straight from the draw.
		_ids = ids;
		_hoverParams = g.Constants<DemoGraph.OutlineParams>("outline.params");
		g.Pass("id").Reads(_viewproj).Writes(ids).Draw(ctx => DemoDraw.Mesh(ctx, scene.Cube, "id", Matrix4x4.Identity, 1));
		g.Readback(ids);

		g.Pass("outline.seed")
			.Samples(ids)
			.Reads(_hoverParams)
			.Writes(seedA)
			.Draw(ctx => ctx.FullScreen("outline.seed", ids));

		var (read, write) = (seedA, seedB);
		const int outlineReach = 4; // a 2-3px ring only needs distance out to its own width: steps 4→2→1
		for (var step = outlineReach; step >= 1; step >>= 1)
		{
			var src = read; // capture per-iteration so the closure follows this step's handle
			float jump = step;
			g.Pass($"outline.jfa{step}")
				.Samples(src)
				.Writes(write)
				.Draw(ctx => ctx.FullScreen("outline.jfa", [jump, world.Width, world.Height], src));
			(read, write) = (write, read); // swap; the graph follows the handles
		}

		var flood = read; // the final flood after the swaps
		g.Pass("outline.resolve")
			.Samples(flood, lit)
			.Writes(outlined)
			.Draw(ctx => ctx.FullScreen("outline.resolve", flood, lit));

		// Nearest upscale from the low-res world to the native screen.
		g.Pass("blit").Samples(outlined).Writes(screen).Draw(ctx => ctx.FullScreen("blit", outlined));
		DemoGraph.Hud(g, screen, scene);
		g.Present(screen);
	});

	// Publish this frame's view-projection for the scene + id passes to read.
	public void Update(Concepts.Camera camera) => _engine.Write(_viewproj, camera.ViewProjection);

	public ResourceHandle? PickTarget => _ids;

	public void WriteHover(IEngine engine, uint hoveredId) =>
		engine.Write(_hoverParams, new DemoGraph.OutlineParams(new Vector4(hoveredId, 0f, 0f, 0f)));
}
