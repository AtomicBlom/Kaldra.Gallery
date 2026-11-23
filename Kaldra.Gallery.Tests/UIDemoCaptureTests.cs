using BinaryVibrance.Kaldra.Backend.DirectX12;
using BinaryVibrance.Kaldra.Backend.Vulkan;
using BinaryVibrance.Kaldra.Core.Hosting;

namespace BinaryVibrance.Kaldra.Tests;

/// <summary>
/// The UI demos are tracked by goldens and checked for Vulkan↔DirectX 12 parity, and both checks pass just as well when
/// the UI is missing: a baseline captured without it locks the omission in, and two backends that both drew no UI
/// agree perfectly. So each UI demo is captured twice on one backend — with its UI, and without it
/// (<c>withUI: false</c>) — and the UI has to change a real share of the frame. GPU-gated — SKIPPED when no backend
/// is available.
/// </summary>
[NotInParallel] // one GPU engine at a time
public class UIDemoCaptureTests
{
	// A pixel counts as changed past the parity bar's per-channel tolerance, and the smallest UI demo's page covers
	// several percent of the frame, so 1% is far above noise and far below any of them.
	private const int PerChannelTolerance = 4;
	private const double MinChangedFraction = 0.01;

	[Test]
	public void Each_UI_demo_draws_its_UI_into_the_capture()
	{
		_ = new VulkanEngineSettings();
		_ = new DirectX12EngineSettings();
		var backend = Backends.Available.FirstOrDefault()
			?? throw new TUnit.Core.Exceptions.SkipTestException("No GPU backend is available on this box (Backends.Available is empty).");

		var failures = new List<string>();
		foreach (var (name, factory) in TrackedDemos.UI)
		{
			var withUI = Path.Combine(Path.GetTempPath(), $"kaldra-uidemo-on-{name}-{Guid.NewGuid():N}.png");
			var withoutUI = Path.Combine(Path.GetTempPath(), $"kaldra-uidemo-off-{name}-{Guid.NewGuid():N}.png");
			try
			{
				GalleryCapture.TryRender(backend, factory, withUI).ShouldBeTrue($"{name} should render on {backend.Name}.");
				GalleryCapture.TryRender(backend, factory, withoutUI, withUI: false).ShouldBeTrue();

				var (w, h, on) = PngReader.Read(withUI);
				var (_, _, off) = PngReader.Read(withoutUI);
				var diff = ImageComparison.Compare(w, h, on, off, PerChannelTolerance);
				if (diff.FailedFraction < MinChangedFraction)
					failures.Add($"{name}: its UI changed only {diff.FailedFraction:P2} of the frame on {backend.Name} ({diff}).");
			}
			finally
			{
				if (File.Exists(withUI)) File.Delete(withUI);
				if (File.Exists(withoutUI)) File.Delete(withoutUI);
			}
		}

		failures.ShouldBeEmpty("A UI demo's capture should show its UI:\n" + string.Join("\n", failures));
	}
}
