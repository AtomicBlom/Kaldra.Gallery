using Loom.Backend.DirectX12;
using Loom.Backend.Vulkan;
using Loom.Core.Hosting;
using Loom.Gallery.Demos;

namespace Loom.Tests;

/// <summary>
/// The cross-backend pixel-parity check — the engine's headline invariant: a <see cref="DemoGallery"/> demo
/// rendered on Vulkan and on DirectX12 must agree within tolerance. Renders each demo offscreen on both backends
/// in-process and diffs the two captures directly (no committed baseline), the in-process replacement for
/// <c>tools/golden.ps1</c>'s cross-backend pass. GPU-gated — SKIPPED unless BOTH backends are usable on this box. A
/// demo whose techniques don't cross-compile on one backend (e.g. a stage with no DXIL on this toolchain) is
/// skipped, not failed — there is simply nothing to compare.
/// </summary>
[NotInParallel] // one GPU engine at a time
public class GalleryParityTests
{
	// Same bar as Scripts/Verification/diff.ps1: a pixel fails if any channel differs by > 4/255, and the frame
	// fails if > 0.5% of pixels are flagged.
	private const int PerChannelTolerance = 4;
	private const double MaxFailedFraction = 0.005;

	// A spread of render demos, plus the UI demos — whose pages GalleryCapture shows chrome-free and whose scripted
	// click it replays, so their frames are as deterministic as the rest; the group-opacity one is the only UI path
	// whose pixels depend on the backend. A demo unsupported on one backend is dropped by the capability filter
	// rather than compared. (The normal-mapping demo is deliberately absent: it
	// diverges ~0.9% of pixels across the backends, just over the strict bar. It isolates to the tangent-space
	// reconstruction in gbuffer_normalmap — the encoded normal alone already diverges ~0.7% while the SAME shader's
	// albedo (a plain texture sample) stays clean at ~0.02%, so it is the normalize/cross/normalize rounding
	// differently between SPIR-V and DXIL, not the sampler; the deferred lighting only nudges 0.7% up to 0.9%
	// (≤68/255, no artifact, both renders correct). TargetedParityTests pins this and bounds the gap. A benign
	// backend precision quirk, not a bug worth gating the demo suite on.)
	private static readonly (string Name, Func<IDemo> Factory)[] Demos =
	[
		("FullReference", () => new FullReferenceDemo()),
		("Bloom", () => new BloomDemo()),
		("Particles", () => new ParticlesDemo()),
		("PixelArt", () => new PixelArtDemo()),
		("StylizedReference", () => new StylizedDemo()),
		("CubeLighting", () => new CubeLightingDemo()),
		("Tonemapping", () => new TonemappingDemo()),
		("Shadows", () => new ShadowsDemo()),
		("Toon", () => new ToonDemo()),
		("UIInteractive", () => new UIInteractiveDemo()),
		("UIGroupOpacity", () => new UIGroupOpacityDemo()),
		("InWorldUI", () => new InWorldUIDemo()),
		("VectorPrims", () => new VectorPrimsDemo()),
		("UILiveTexture", () => new UILiveTextureDemo()),
	];

	[Test]
	public void Vulkan_and_DirectX12_renders_match_within_tolerance()
	{
		// Touch each backend's settings subclass so its module initializer self-registers (guarded by IsSupported()).
		_ = new VulkanEngineSettings();
		_ = new DirectX12EngineSettings();
		var vulkan = Backends.Available.FirstOrDefault(e => e.Name == "Vulkan");
		var dx12 = Backends.Available.FirstOrDefault(e => e.Name == "DirectX12");
		if (vulkan is null || dx12 is null)
			throw new TUnit.Core.Exceptions.SkipTestException("Cross-backend parity needs BOTH Vulkan and DirectX12 available on this box.");

		var compared = 0;
		var failures = new List<string>();
		foreach (var (name, factory) in Demos)
		{
			var vkPath = Path.Combine(Path.GetTempPath(), $"loom-parity-vk-{name}-{Guid.NewGuid():N}.png");
			var dxPath = Path.Combine(Path.GetTempPath(), $"loom-parity-dx-{name}-{Guid.NewGuid():N}.png");
			try
			{
				// A demo missing on one backend (no DXIL for a stage on this toolchain) is not a parity failure —
				// render both and only compare when both produced a frame.
				if (!GalleryCapture.TryRender(vulkan, factory, vkPath) | !GalleryCapture.TryRender(dx12, factory, dxPath))
					continue;

				var (vw, vh, vk) = PngReader.Read(vkPath);
				var (dw, dh, dx) = PngReader.Read(dxPath);
				if (vw != dw || vh != dh)
				{
					failures.Add($"{name}: size {vw}x{vh} (Vulkan) != {dw}x{dh} (DirectX12)");
					continue;
				}

				var diff = ImageComparison.Compare(vw, vh, vk, dx, PerChannelTolerance);
				compared++;
				if (diff.FailedFraction > MaxFailedFraction)
					failures.Add($"{name}: {diff}");
			}
			finally
			{
				if (File.Exists(vkPath)) File.Delete(vkPath);
				if (File.Exists(dxPath)) File.Delete(dxPath);
			}
		}

		// Guard against a vacuous pass: if every demo was dropped on one backend, nothing was checked.
		compared.ShouldBeGreaterThan(0, "no demo rendered on both backends — nothing was compared for parity.");
		failures.ShouldBeEmpty("Cross-backend (Vulkan vs DirectX12) parity failures:\n  " + string.Join("\n  ", failures));
	}
}
