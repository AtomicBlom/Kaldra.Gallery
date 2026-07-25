using Loom.Backend.DirectX12;
using Loom.Backend.Vulkan;
using Loom.Core.Hosting;
using Loom.Gallery.Demos;

namespace Loom.Tests;

/// <summary>
/// A controlled render test of a different flavour from <see cref="TargetedParityTests"/> (which diffs backends
/// against each other): here the assertion is self-consistency — the offscreen render path must be
/// <em>deterministic</em>, reproducing the same frame exactly when a scene is rendered twice on the same backend.
/// This is the invariant the golden-image tests rest on (they compare a freshly captured frame against a committed
/// baseline); if a backend's render drifted run-to-run, the goldens would flake and mean nothing. GPU-gated —
/// SKIPPED when no backend is available; each available backend is checked independently.
/// </summary>
[NotInParallel] // one GPU engine at a time
public class RenderDeterminismTests
{
	[Test]
	public void The_full_reference_frame_renders_identically_on_repeat()
	{
		_ = new VulkanEngineSettings();
		_ = new DirectX12EngineSettings();
		if (Engine.AvailableEngines.Count == 0)
			throw new TUnit.Core.Exceptions.SkipTestException("no GPU backend available on this box.");

		var failures = new List<string>();
		foreach (var backend in Engine.AvailableEngines)
		{
			var first = Path.Combine(Path.GetTempPath(), $"loom-det-{backend.Name}-a-{Guid.NewGuid():N}.png");
			var second = Path.Combine(Path.GetTempPath(), $"loom-det-{backend.Name}-b-{Guid.NewGuid():N}.png");
			try
			{
				// The full reference frame is the busiest scene (deferred lighting, shadows, SSAO, particles, bloom,
				// tone-map) — if it reproduces exactly, the simpler goldens do too.
				if (!GalleryCapture.TryRender(backend, () => new FullReferenceDemo(), first) ||
					!GalleryCapture.TryRender(backend, () => new FullReferenceDemo(), second))
					continue; // unsupported on this backend/toolchain — nothing to check

				var (w, h, a) = PngReader.Read(first);
				var (_, _, b) = PngReader.Read(second);
				// Tolerance 0: a repeat render on the same backend must be bit-identical, not merely close.
				var diff = ImageComparison.Compare(w, h, a, b, 0);
				if (diff.FailedFraction > 0)
					failures.Add($"{backend.Name}: repeat render is not identical: {diff}");
			}
			finally
			{
				if (File.Exists(first)) File.Delete(first);
				if (File.Exists(second)) File.Delete(second);
			}
		}

		failures.ShouldBeEmpty("Offscreen render is not deterministic within a backend:\n  " + string.Join("\n  ", failures));
	}
}
