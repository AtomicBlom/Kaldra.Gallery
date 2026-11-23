using BinaryVibrance.Kaldra.Backend.DirectX12;
using BinaryVibrance.Kaldra.Backend.Vulkan;
using BinaryVibrance.Kaldra.Core.Hosting;
using Kaldra.Gallery.Demos;

namespace BinaryVibrance.Kaldra.Tests;

/// <summary>
/// In-process golden-image regression: renders the tracked <see cref="DemoGallery"/> demos offscreen (no window,
/// no capture tooling) and diffs each against a committed baseline under <c>Goldens/</c>. GPU-gated — SKIPPED when
/// no Vulkan backend is usable on this box (the baselines are Vulkan captures; DirectX12 is covered by
/// <see cref="GalleryParityTests"/>). Set <c>KALDRA_GOLDEN_UPDATE</c> to regenerate the committed baselines from a
/// fresh render instead of comparing (the in-process replacement for <c>tools/golden.ps1 -Update</c>).
/// </summary>
[NotInParallel] // one GPU engine at a time
public class GalleryGoldenTests
{
	// A captured frame may differ from its baseline by a hair (driver rounding); a real regression moves many
	// pixels. Flag a pixel if any channel differs by > 4/255, and fail only if > 0.5% of pixels are flagged.
	private const int PerChannelTolerance = 4;
	private const double MaxFailedFraction = 0.005;

	[Test]
	public void Golden_demos_match_committed_baselines()
	{
		// Touch each backend's settings subclass so its module initializer self-registers (guarded by IsSupported()).
		_ = new VulkanEngineSettings();
		_ = new DirectX12EngineSettings();
		var vulkan = Backends.Available.FirstOrDefault(e => e.Name == "Vulkan")
			?? throw new TUnit.Core.Exceptions.SkipTestException("No Vulkan backend is available on this box — the golden baselines are Vulkan captures.");

		var update = !string.IsNullOrEmpty(Environment.GetEnvironmentVariable("KALDRA_GOLDEN_UPDATE"));
		if (update)
			Directory.CreateDirectory(SourceGoldensDir);

		var failures = new List<string>();
		foreach (var (name, factory) in TrackedDemos.All)
		{
			if (update)
			{
				// Regenerate the committed baseline in the source tree (RequestCapture is the PNG encoder).
				var srcPath = Path.Combine(SourceGoldensDir, name + ".png");
				if (!GalleryCapture.TryRender(vulkan, factory, srcPath))
					throw new InvalidOperationException($"{name}: did not render on Vulkan (techniques unsupported?).");
				Console.WriteLine($"[golden] regenerated {srcPath}");
				continue;
			}

			var baselinePath = Path.Combine(OutputGoldensDir, name + ".png");
			if (!File.Exists(baselinePath))
			{
				failures.Add($"{name}: no committed baseline (looked in {baselinePath})");
				continue;
			}

			var capturePath = Path.Combine(Path.GetTempPath(), $"kaldra-golden-{name}-{Guid.NewGuid():N}.png");
			try
			{
				if (!GalleryCapture.TryRender(vulkan, factory, capturePath))
				{
					failures.Add($"{name}: did not render on Vulkan (techniques unsupported?)");
					continue;
				}

				var (bw, bh, baseline) = PngReader.Read(baselinePath);
				var (cw, ch, capture) = PngReader.Read(capturePath);
				if (bw != cw || bh != ch)
				{
					failures.Add($"{name}: size {cw}x{ch} != baseline {bw}x{bh}");
					continue;
				}

				var diff = ImageComparison.Compare(bw, bh, baseline, capture, PerChannelTolerance);
				if (diff.FailedFraction > MaxFailedFraction)
					failures.Add($"{name}: {diff}");
			}
			finally
			{
				if (File.Exists(capturePath))
					File.Delete(capturePath);
			}
		}

		if (update)
			throw new TUnit.Core.Exceptions.SkipTestException($"KALDRA_GOLDEN_UPDATE set — regenerated {TrackedDemos.All.Length} baselines in {SourceGoldensDir} instead of comparing.");

		failures.ShouldBeEmpty("Gallery golden-image regressions detected:\n  " + string.Join("\n  ", failures));
	}

	// Committed baselines, copied beside the test assembly as Content — compared against here.
	private static string OutputGoldensDir => Path.Combine(AppContext.BaseDirectory, "Goldens");

	// The source-tree Goldens/ (bin/<config>/net11.0 → up three → the project dir), where UPDATE writes so a
	// regenerated baseline is what gets committed and re-copied to the output on the next build.
	private static string SourceGoldensDir => Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "Goldens"));
}
