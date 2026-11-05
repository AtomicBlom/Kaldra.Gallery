using Loom.Backend.DirectX12;
using Loom.Backend.Vulkan;
using Loom.Core.Hosting;
using Loom.Gallery.Demos;

namespace Loom.Tests;

/// <summary>
/// Multisampling and its derived resolve, as a property: the MSAA demo drawn with four samples a pixel and then with one
/// (its control) shows the same scene, but the four-sample frame's edges — the cube's, and the grille's alpha-to-coverage
/// bars — are blended, so it has many more pixels part way between their neighbours. A resolve that never ran would
/// sample nothing (a blank frame); one that sampled a single sample would match the control. The frames are kept when
/// <c>LOOM_KEEP_CAPTURES</c> is set. GPU-gated — SKIPPED when no backend is available.
/// </summary>
[NotInParallel] // one GPU engine at a time
public class MsaaTests
{
	[Test]
	public void Four_samples_blend_the_edges_one_sample_steps()
	{
		_ = new VulkanEngineSettings();
		_ = new DirectX12EngineSettings();
		if (Backends.Available.Count == 0)
			throw new TUnit.Core.Exceptions.SkipTestException("No GPU backend is available on this box (Backends.Available is empty).");
		var keep = Environment.GetEnvironmentVariable("LOOM_KEEP_CAPTURES") is not null;

		foreach (var backend in Backends.Available)
		{
			var four = Path.Combine(Path.GetTempPath(), $"loom-msaa-{backend.Name}-4x.png");
			var one = Path.Combine(Path.GetTempPath(), $"loom-msaa-{backend.Name}-1x.png");
			try
			{
				if (!GalleryCapture.TryRender(backend, () => new MsaaDemo(4), four, withUI: false)
					|| !GalleryCapture.TryRender(backend, () => new MsaaDemo(1), one, withUI: false))
					continue;
				var (w, h, msaa) = PngReader.Read(four);
				var (_, _, plain) = PngReader.Read(one);
				var blended = Blended(w, h, msaa);
				var stepped = Blended(w, h, plain);
				// A stepped edge leaves next to no pixel part way between its neighbours; a blended one leaves a line of them
				blended.ShouldBeGreaterThan(1000, $"{backend.Name}: four samples left only {blended} blended edge pixels; the edges weren't resolved.");
				blended.ShouldBeGreaterThan(stepped * 3, $"{backend.Name}: four samples blended {blended} edge pixels against one sample's {stepped}.");
			}
			finally
			{
				if (!keep)
				{
					File.Delete(four);
					File.Delete(one);
				}
			}
		}
	}

	// Pixels that lie part way between the neighbours either side of them, horizontally or vertically, by a margin: what
	// an edge blended across its pixel leaves, and a stepped one doesn't
	private static int Blended(int width, int height, byte[] rgba)
	{
		int Luma(int x, int y)
		{
			var i = ((y * width) + x) * 4;
			return (rgba[i] * 3) + (rgba[i + 1] * 6) + rgba[i + 2];
		}

		var count = 0;
		for (var y = 1; y < height - 1; y++)
			for (var x = 1; x < width - 1; x++)
			{
				int c = Luma(x, y);
				if (Between(Luma(x - 1, y), c, Luma(x + 1, y)) || Between(Luma(x, y - 1), c, Luma(x, y + 1)))
					count++;
			}
		return count;
	}

	private static bool Between(int a, int c, int b)
	{
		int low = Math.Min(a, b), high = Math.Max(a, b);
		return high - low > 300 && c > low + 60 && c < high - 60;
	}
}
