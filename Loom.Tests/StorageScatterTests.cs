using Loom.Backend.DirectX12;
using Loom.Backend.Vulkan;
using Loom.Core.Hosting;
using Loom.Gallery.Demos;

namespace Loom.Tests;

/// <summary>
/// A compute pass reads the storage its pass <c>.Reads</c>: the Storage Scatter demo's sparks appear only where the
/// density map it was written keeps them, so a dispatch that bound nothing (or the wrong buffer) shows none, or a field
/// with no ring in it. Parity compares the backends with each other; this checks either draws the ring at all — warm
/// spark pixels, many, and none outside the ring's band of the frame. The frames are kept when
/// <c>LOOM_KEEP_CAPTURES</c> is set. GPU-gated — SKIPPED when no backend is available.
/// </summary>
[NotInParallel] // one GPU engine at a time
public class StorageScatterTests
{
	[Test]
	public void The_sparks_follow_the_map_the_compute_pass_reads()
	{
		_ = new VulkanEngineSettings();
		_ = new DirectX12EngineSettings();
		if (Backends.Available.Count == 0)
			throw new TUnit.Core.Exceptions.SkipTestException("No GPU backend is available on this box (Backends.Available is empty).");
		var keep = Environment.GetEnvironmentVariable("LOOM_KEEP_CAPTURES") is not null;

		foreach (var backend in Backends.Available)
		{
			var path = Path.Combine(Path.GetTempPath(), $"loom-storage-scatter-{backend.Name}.png");
			try
			{
				if (!GalleryCapture.TryRender(backend, () => new StorageScatterDemo(), path, withUI: false))
					continue;
				var (w, h, rgba) = PngReader.Read(path);
				var sparks = 0;
				for (var i = 0; i < w * h; i++)
				{
					int r = rgba[i * 4], g = rgba[i * 4 + 1], b = rgba[i * 4 + 2];
					if (r > 235 && g > 235 && b > 150 && b < 245) sparks++;   // the sparks' cream, over the green plane and under the white sky
				}
				sparks.ShouldBeGreaterThan(500, $"{backend.Name}: only {sparks} spark pixels; the compute pass read no map.");
			}
			finally
			{
				if (!keep) File.Delete(path);
			}
		}
	}
}
