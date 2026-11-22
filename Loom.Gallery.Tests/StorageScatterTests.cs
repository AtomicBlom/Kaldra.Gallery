using Loom.Backend.DirectX12;
using Loom.Backend.Vulkan;
using Loom.Core.Hosting;
using Loom.Gallery.Demos;

namespace Loom.Tests;

/// <summary>
/// A compute pass reads the storage its pass <c>.Reads</c>: the Storage Scatter demo's sparks appear only where the
/// density map it was written keeps them. Its ring is held to a control, the same demo with a map of density 1
/// everywhere: the ring keeps a fraction of the field's sparks, where a dispatch that bound the wrong buffer, or read
/// the map as anything else, would scatter the two alike (or none). Parity compares the backends with each other; this
/// checks either reads the map at all. The frames are kept when <c>LOOM_KEEP_CAPTURES</c> is set. GPU-gated — SKIPPED
/// when no backend is available.
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

		var rendered = 0;
		foreach (var backend in Backends.Available)
		{
			var ringPath = Path.Combine(Path.GetTempPath(), $"loom-storage-scatter-{backend.Name}.png");
			var fieldPath = Path.Combine(Path.GetTempPath(), $"loom-storage-scatter-{backend.Name}-filled.png");
			try
			{
				if (!GalleryCapture.TryRender(backend, () => new StorageScatterDemo(), ringPath, withUI: false)
					|| !GalleryCapture.TryRender(backend, () => new StorageScatterDemo(filled: true), fieldPath, withUI: false))
					continue;
				rendered++;
				var ring = Sparks(ringPath);
				var field = Sparks(fieldPath);
				ring.ShouldBeGreaterThan(500, $"{backend.Name}: only {ring} spark pixels; the compute pass read no map.");
				// The ring averages under a quarter of the field's density; half leaves room for sparks overlapping
				ring.ShouldBeLessThan(field / 2, $"{backend.Name}: the ring lit {ring} spark pixels against the filled map's {field}; the compute pass didn't read the ring.");
			}
			finally
			{
				if (!keep)
				{
					File.Delete(ringPath);
					File.Delete(fieldPath);
				}
			}
		}
		// A backend that couldn't render is skipped above; all of them skipped would otherwise pass with nothing checked
		rendered.ShouldBeGreaterThan(0, "no backend rendered the Storage Scatter demo.");
	}

	// The sparks' cream pixels, over the green plane and under the white sky
	private static int Sparks(string path)
	{
		var (w, h, rgba) = PngReader.Read(path);
		var sparks = 0;
		for (var i = 0; i < w * h; i++)
		{
			int r = rgba[i * 4], g = rgba[i * 4 + 1], b = rgba[i * 4 + 2];
			if (r > 235 && g > 235 && b > 150 && b < 245) sparks++;
		}
		return sparks;
	}
}
