using BinaryVibrance.Kaldra.Backend.DirectX12;
using BinaryVibrance.Kaldra.Core.Hosting;

namespace BinaryVibrance.Kaldra.Tests;

/// <summary>
/// The GPU-less software-rasterizer smoke: forces the DirectX12 backend onto WARP (Microsoft's software D3D12
/// rasterizer, present on any Windows host) via <see cref="EngineSettings.PreferSoftwareAdapter"/> and renders each
/// tracked demo offscreen, asserting the frame isn't near-empty (a distinct-colour count — a real scene lands in the
/// thousands, a crash or black frame collapses toward one). It is the in-process replacement for the retired sandbox
/// <c>--headless --capture</c> + colorcount loop that <c>.github/workflows/software-capture.yml</c> drove: the
/// reusable desktop host carries no capture flags (capture is a test concern, not a production-host one), and no real
/// GPU is needed, so "every tracked demo still renders under software rasterization" is proven here instead.
/// <para>
/// SKIPPED when DirectX12 is unavailable (a non-Windows box) — WARP is a D3D12 feature. GPU-based validation is
/// forced off: it patches shaders in a way WARP does not reliably support and can crash the process, so the test
/// stays safe in Debug too rather than depending on a Release build.
/// </para>
/// </summary>
[NotInParallel] // one GPU engine at a time
public class GallerySoftwareAdapterTests
{
	// A real scene renders thousands of distinct colours; a crashed/black/flat frame collapses toward one. The floor
	// is deliberately loose — this is a "did it render at all" smoke, not a fidelity gate (that is GalleryParityTests).
	private const int MinDistinctColors = 10;

	[Test]
	public void Tracked_demos_render_under_software_rasterization()
	{
		// Touch the DirectX12 settings subclass so its module initializer self-registers the backend (guarded by its
		// own IsSupported() probe). WARP is a DirectX12 feature and a Vulkan CPU device is not guaranteed on a box, so
		// this smoke pins to DirectX12 + WARP exactly as the retired workflow did.
		_ = new DirectX12EngineSettings();
		var dx12 = Backends.Available.FirstOrDefault(e => e.Name == "DirectX12")
			?? throw new TUnit.Core.Exceptions.SkipTestException("No DirectX12 backend is available on this box — WARP is a DirectX12 (Windows) feature.");

		// Force WARP, and force GPU-based validation off (it can crash the process on WARP) so the smoke is safe in
		// any build configuration.
		var warp = new DirectX12EngineSettings { PreferSoftwareAdapter = true, GpuBasedValidation = false };

		var rendered = 0;
		var failures = new List<string>();
		foreach (var (name, factory) in TrackedDemos.All)
		{
			var capturePath = Path.Combine(Path.GetTempPath(), $"kaldra-warp-{name}-{Guid.NewGuid():N}.png");
			try
			{
				// A demo whose techniques have no DXIL on this toolchain is skipped, not failed — nothing rendered.
				if (!GalleryCapture.TryRender(dx12, factory, capturePath, warp))
					continue;

				var (_, _, rgba) = PngReader.Read(capturePath);
				rendered++;
				var distinct = ImageComparison.DistinctColors(rgba);
				if (distinct < MinDistinctColors)
					failures.Add($"{name}: rendered a near-empty frame under WARP ({distinct} distinct colours).");
			}
			finally
			{
				if (File.Exists(capturePath))
					File.Delete(capturePath);
			}
		}

		// Guard against a vacuous pass: if every demo was dropped, nothing was actually smoke-tested.
		rendered.ShouldBeGreaterThan(0, "no tracked demo rendered under WARP — nothing was smoke-tested.");
		failures.ShouldBeEmpty("Software-rasterizer (DirectX12 + WARP) smoke failures:\n  " + string.Join("\n  ", failures));
	}

}
