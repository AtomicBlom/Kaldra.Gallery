using Loom.Backend.DirectX12;
using Loom.Backend.Vulkan;
using Loom.Core.Graph;
using Loom.Core.Hosting;
using Loom.Core.Materials;
using Loom.Gallery.Demos;

namespace Loom.Tests;

/// <summary>
/// A compute pass's <c>.Reads</c> Constants bind to its technique's constant buffers, as a draw's do — what the Particles
/// demo's golden and parity show working. This is the other half: a compute technique that declares a constant buffer
/// its pass does not read must throw at dispatch rather than dispatch with the slot unbound, which reads garbage on one
/// backend and fails validation on another. GPU-gated — SKIPPED when no backend is available.
/// </summary>
[NotInParallel] // one GPU engine at a time
public class ComputeConstantsTests
{
	private const int W = 64, H = 64;

	[Test]
	public void A_compute_technique_declaring_a_block_its_pass_does_not_read_throws()
	{
		foreach (var backend in AvailableBackends())
		{
			using var engine = backend.Create(Surface.Offscreen(W, H), W, H, new EngineSettings());
			engine.DefineTechnique("fountain", TechniqueDescriptor.Compute("particle_fountain", hasFragmentPush: true));
			engine.SetGraph(RenderGraph.Define(g =>
			{
				var particles = g.Buffer("particles", 64, SandboxVertexLayouts.ParticlePosition);
				g.Compute("fountain")
					.ReadWrites(particles)   // and no .Reads of the fountain's block
					.Dispatch(ctx => ctx.Dispatch("fountain", (1, 1, 1), [0f], [], [particles]));
				g.Pass("consume").ReadsVertex(particles).Writes(g.Backbuffer).Draw(_ => { });
				g.Present(g.Backbuffer);
			}));

			var thrown = Should.Throw<Exception>(() => engine.Render(new FrameInputs { Time = 0, DeltaSeconds = 1.0 / 60.0 }));
			Flatten(thrown).ShouldContain(e => e is InvalidOperationException && e.Message.Contains("constant buffer"),
				$"{backend.Name}: expected the dispatch to refuse the unread block, got: {thrown}");
		}
	}

	// And its .Reads Storage likewise: a compute technique reading a read-only storage buffer its pass does not .Reads
	// throws at dispatch, on every backend, rather than dispatching with the slot unbound (the Storage Scatter demo's
	// parity shows the bound case working)
	[Test]
	public void A_compute_technique_reading_storage_its_pass_does_not_read_throws()
	{
		foreach (var backend in AvailableBackends())
		{
			using var engine = backend.Create(Surface.Offscreen(W, H), W, H, new EngineSettings());
			engine.DefineTechnique("expand", TechniqueDescriptor.Compute("storage_scatter", hasFragmentPush: true));
			engine.SetGraph(RenderGraph.Define(g =>
			{
				var sparks = g.Buffer("sparks", 96 * 96, SandboxVertexLayouts.ParticlePosition);
				g.Compute("expand")
					.ReadWrites(sparks)   // and no .Reads of the density map
					.Dispatch(ctx => ctx.Dispatch("expand", (144, 1, 1), [0f], [], [sparks]));
				g.Pass("consume").ReadsVertex(sparks).Writes(g.Backbuffer).Draw(_ => { });
				g.Present(g.Backbuffer);
			}));

			var thrown = Should.Throw<Exception>(() => engine.Render(new FrameInputs { Time = 0, DeltaSeconds = 1.0 / 60.0 }));
			Flatten(thrown).ShouldContain(e => e is InvalidOperationException && e.Message.Contains("storage buffer"),
				$"{backend.Name}: expected the dispatch to refuse the unread storage, got: {thrown}");
		}
	}

	private static IEnumerable<Exception> Flatten(Exception e)
	{
		for (var x = e; x != null; x = x.InnerException)
		{
			yield return x;
			if (x is AggregateException a)
				foreach (var inner in a.InnerExceptions.SelectMany(Flatten))
					yield return inner;
		}
	}

	private static List<IRenderBackend> AvailableBackends()
	{
		_ = new VulkanEngineSettings();
		_ = new DirectX12EngineSettings();
		if (Backends.Available.Count == 0)
			throw new TUnit.Core.Exceptions.SkipTestException("No GPU backend is available on this box (Backends.Available is empty).");
		return [.. Backends.Available];
	}
}
