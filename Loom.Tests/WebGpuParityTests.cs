using System.Diagnostics;

using Loom.Backend.Vulkan;
using Loom.Core.Hosting;
using Loom.Gallery.Demos;

namespace Loom.Tests;

/// <summary>
/// The third backend's parity leg: a demo rendered in a browser on WebGPU must agree with the same demo rendered
/// on Vulkan. <see cref="GalleryParityTests"/> pairs the two desktop backends in-process; this pairs the browser
/// against Vulkan, which is the only automated check that WebGPU pixel drift is noticed at all — until now it was
/// caught by eye, if at all.
/// <para>
/// It has to be out-of-process: <c>Loom.Tests</c> cannot reference <c>Loom.Web</c> (that needs the wasm SDK
/// workload, which would drag the browser toolchain into every desktop test run), and WebGPU only exists inside a
/// browser. So the browser half is driven by <c>Scripts/Verification/web-capture.ps1</c>, which serves a published
/// <c>Loom.Web</c> and collects the PNG the page POSTs back, and the Vulkan half is rendered here through the same
/// <see cref="GalleryCapture"/> the desktop parity test uses. Both halves render 1280×720 at frame 60 on a frozen
/// clock, which is what makes them comparable at all.
/// </para>
/// <para>
/// Publishing <c>Loom.Web</c> takes minutes and needs the <c>wasm-tools</c> workload, so this test does not publish
/// — point <c>LOOM_WEB_PUBLISH</c> at a publish directory and it runs, otherwise it SKIPs. <c>tools/web-parity.ps1</c>
/// does the publish and sets the variable.
/// </para>
/// </summary>
[NotInParallel] // brings a Vulkan device up, and drives a browser that wants the GPU at the same time
public class WebGpuParityTests
{
	// The same bar as GalleryParityTests and Scripts/Verification/diff.ps1: a pixel fails if ANY channel differs by
	// more than this, and the frame fails if more than MaxFailedFraction of pixels are flagged. Deliberately not
	// loosened for the browser — a backend that needs a wider tolerance should be excluded by name, with its
	// measured number recorded, rather than having every demo's guard quietly weakened to accommodate it.
	private const int PerChannelTolerance = 4;
	private const double MaxFailedFraction = 0.005;

	// Two blank frames compare as identical, so without this floor a browser that failed to initialise WebGPU and a
	// Vulkan render that drew nothing would "pass". A blank frame holds one or two colours; most checked demos render
	// a thousand or more. The flattest is UIGroupOpacity — a flat-lit cube and plane under a panel of solid rounded
	// rects — at 44 on Vulkan and 62 on WebGPU (RTX 2080 Ti), because the UI's one-pixel edge ramps add few shades.
	// The floor sits well above blank and below that. A UI that vanished from both frames still fails, by
	// UIDemoCaptureTests on the Vulkan side.
	private const int MinDistinctColors = 16;

	/// <summary>
	/// Demos checked for Vulkan↔WebGPU parity: the web gallery's key (the <c>?capture=</c> value) beside the desktop
	/// factory that renders the reference. The names must match <c>Loom.Web</c>'s <c>WebGallery</c> keys exactly —
	/// an unknown key silently resolves to the default demo rather than erroring, so a typo shows up as a wild
	/// mismatch rather than a missing capture. The UI demos compare because both sides show the same tree the same
	/// way: the demo's own page in a chrome-hidden shell, and one scripted click the demo derives from that frame's
	/// layout and camera (<see cref="IDemo.CaptureClick"/>), so it lands on its button although the browser's capture
	/// clock advances where the desktop one holds still.
	/// </summary>
	private static readonly (string WebKey, string Name, Func<IDemo> Factory)[] Demos =
	[
		("pixelart", "PixelArt", () => new PixelArtDemo()),
		("bloom", "Bloom", () => new BloomDemo()),
		("particles", "Particles", () => new ParticlesDemo()),
		("uiinteractive", "UIInteractive", () => new UIInteractiveDemo()),
		("uigroupopacity", "UIGroupOpacity", () => new UIGroupOpacityDemo()),
		("inworldui", "InWorldUI", () => new InWorldUIDemo()),
		("vectorprims", "VectorPrims", () => new VectorPrimsDemo()),
	];

	// Demos deliberately NOT checked, with the reason recorded rather than the tolerance widened — the numbers are
	// the point, and a silent allow-list would hide them growing. Measured against a Vulkan reference on an
	// Adreno X1-85, browser adapter "qualcomm adreno-7xx" (hardware, not a software fallback):
	//
	//   fullreference  maxDiff=128, mean 2.330, 20.38% of pixels over 4/255
	//   stylized       maxDiff=160, mean 11.786, 49.39% of pixels over 4/255
	//
	// Those are far past what precision explains. WGSL and SPIR-V do disagree more than SPIR-V and DXIL do, but the
	// worst desktop pairing on this box (NormalMapping, excluded from GalleryParityTests) is 0.9% — nothing about
	// rounding moves half a frame. They are functional divergences to find and fix, not tolerance to relax.
	//
	//   cube           matches within tolerance, but its frame holds only 5 distinct colours (a flat-shaded cube
	//                  on a ground plane), so it cannot clear the near-empty guard below and would add no signal
	//                  if it did: two flat frames compare equal whether or not the backend works.

	[Test]
	public void WebGPU_renders_match_Vulkan_within_tolerance()
	{
		_ = new VulkanEngineSettings(); // touch the settings type so the backend's module initializer registers it
		var vulkan = Backends.Available.FirstOrDefault(e => e.Name == "Vulkan")
			?? throw new TUnit.Core.Exceptions.SkipTestException("WebGPU parity needs Vulkan available for the reference render.");

		var publishDir = Environment.GetEnvironmentVariable("LOOM_WEB_PUBLISH");
		if (string.IsNullOrWhiteSpace(publishDir) || !Directory.Exists(publishDir))
			throw new TUnit.Core.Exceptions.SkipTestException(
				"Set LOOM_WEB_PUBLISH to a published Loom.Web directory to run the WebGPU parity leg (tools/web-parity.ps1 does this).");

		var script = Path.Combine(RepositoryRoot(), "Scripts", "Verification", "web-capture.ps1");
		if (!File.Exists(script))
			throw new TUnit.Core.Exceptions.SkipTestException($"The capture harness is not next to this test run ({script} does not exist).");

		var shotDir = Path.Combine(Path.GetTempPath(), $"loom-web-parity-{Guid.NewGuid():N}");
		Directory.CreateDirectory(shotDir);
		try
		{
			var (exitCode, output) = RunCaptureHarness(script, publishDir, shotDir);

			// The harness's exit codes say whether this box CAN run the leg or whether the leg FAILED, which are very
			// different answers: a machine with no Chromium or no publish cannot check anything and must skip.
			if (exitCode == 2)
				throw new TUnit.Core.Exceptions.SkipTestException("No Chromium browser on this box — nothing can drive WebGPU here.\n" + output);
			if (exitCode == 3)
				throw new TUnit.Core.Exceptions.SkipTestException("LOOM_WEB_PUBLISH does not point at a published Loom.Web.\n" + output);

			var shots = Demos.Select(d => (d.WebKey, d.Name, d.Factory, Path: Path.Combine(shotDir, d.WebKey + ".png")))
				.Where(s => File.Exists(s.Path))
				.ToArray();

			// A browser that cannot get a WebGPU adapter throws on the page and never POSTs, so EVERY demo times out.
			// That is a capability gap, not a regression — skip. A partial result is a real failure: the browser
			// clearly works, so a demo that produced nothing is a demo that broke.
			if (shots.Length == 0)
				throw new TUnit.Core.Exceptions.SkipTestException(
					"The browser produced no captures at all — it most likely has no WebGPU adapter here.\n" + output);

			var adapterFile = Path.Combine(shotDir, "adapter.txt");
			var adapter = File.Exists(adapterFile) ? File.ReadAllText(adapterFile) : "unknown";

			var compared = 0;
			var failures = new List<string>();
			foreach (var (webKey, name, factory, webPath) in shots)
			{
				var vkPath = Path.Combine(shotDir, $"vk-{webKey}.png");
				// A demo whose techniques don't cross-compile on this Vulkan toolchain has no reference to compare
				// against — the same capability filter GalleryParityTests applies.
				if (!GalleryCapture.TryRender(vulkan, factory, vkPath))
					continue;

				var (ww, wh, web) = PngReader.Read(webPath);
				var (vw, vh, vk) = PngReader.Read(vkPath);
				if (ww != vw || wh != vh)
				{
					failures.Add($"{name}: size {ww}x{wh} (WebGPU) != {vw}x{vh} (Vulkan)");
					continue;
				}

				var webColors = ImageComparison.DistinctColors(web);
				var vkColors = ImageComparison.DistinctColors(vk);
				if (webColors < MinDistinctColors || vkColors < MinDistinctColors)
				{
					failures.Add($"{name}: near-empty frame ({webColors} distinct colours on WebGPU, {vkColors} on Vulkan) — " +
						"a blank frame compares equal to another blank frame, so this is not a parity result.");
					continue;
				}

				compared++;
				var diff = ImageComparison.Compare(ww, wh, web, vk, PerChannelTolerance);
				if (diff.FailedFraction > MaxFailedFraction)
					failures.Add($"{name}: {diff}");
			}

			// Guard against a vacuous pass: if every demo was dropped for a missing reference, nothing was checked.
			compared.ShouldBeGreaterThan(0, "no demo rendered on both WebGPU and Vulkan — nothing was compared for parity.");

			var missing = Demos.Select(d => d.WebKey).Except(shots.Select(s => s.WebKey)).ToArray();
			missing.ShouldBeEmpty(
				$"The browser captured some demos but not {string.Join(", ", missing)} — that demo faults in the browser.\n" + output);

			failures.ShouldBeEmpty(
				$"WebGPU (browser adapter: {adapter}) vs Vulkan parity failures:\n  " + string.Join("\n  ", failures));
		}
		finally
		{
			if (Directory.Exists(shotDir))
				Directory.Delete(shotDir, recursive: true);
		}
	}

	// Drive the browser once for every demo. One harness invocation rather than one per demo: it serves the app from
	// a single listener and, more importantly, owns browser cleanup across the whole run — a stray headless browser
	// silently breaks every later capture by holding the profile directory the next launch reuses.
	private static (int ExitCode, string Output) RunCaptureHarness(string script, string publishDir, string shotDir)
	{
		var names = string.Join(",", Demos.Select(d => d.WebKey));
		var info = new ProcessStartInfo("pwsh")
		{
			RedirectStandardOutput = true,
			RedirectStandardError = true,
			UseShellExecute = false,
		};
		foreach (var arg in new[]
		{
			"-NoProfile", "-File", script,
			"-PublishDir", publishDir, "-Names", names, "-OutDir", shotDir,
		})
			info.ArgumentList.Add(arg);

		try
		{
			using var process = Process.Start(info)
				?? throw new TUnit.Core.Exceptions.SkipTestException("pwsh could not be started to drive the capture harness.");
			var stdout = process.StandardOutput.ReadToEnd();
			var stderr = process.StandardError.ReadToEnd();
			process.WaitForExit();
			return (process.ExitCode, (stdout + stderr).Trim());
		}
		catch (System.ComponentModel.Win32Exception)
		{
			throw new TUnit.Core.Exceptions.SkipTestException("PowerShell 7 (pwsh) is not on PATH, so the capture harness cannot run.");
		}
	}

	// bin/<config>/<tfm> → up three to the project, up one more to the repository root.
	private static string RepositoryRoot() =>
		Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..", "..", "..", ".."));
}
