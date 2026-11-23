using BinaryVibrance.Kaldra.Backend.Vulkan;
using BinaryVibrance.Kaldra.Core.Materials;

namespace BinaryVibrance.Kaldra.Tests;

/// <summary>
/// <see cref="SpirvEntryPoints.Resolve"/> against the gallery's real compute shaders (its unit tests, on hand-built
/// modules, are the engine's: SpirvEntryPointsTests). slangc renames a lone entry point to <c>main</c> in SPIR-V, which
/// the resolution exists for.
/// </summary>
public class SpirvComputeArtifactsTests
{
	[Test]
	public void The_real_compute_artifacts_still_rename_their_lone_entry_to_main()
	{
		// Pins the behaviour the resolution exists for. If a future slangc stops renaming, this fails and the fallback
		// path becomes dead code that should be reconsidered — rather than silently never running again.
		var shaders = Path.Combine(AppContext.BaseDirectory, "shaders");
		if (!Directory.Exists(shaders))
			throw new TUnit.Core.Exceptions.SkipTestException($"No compiled shaders beside the test assembly ({shaders}).");

		var computeShaders = new[] { "exposure", "skin", "particle_update", "particle_glow_update", "firefly_update" };
		var probed = 0;
		var failures = new List<string>();
		foreach (var name in computeShaders)
		{
			var path = Path.Combine(shaders, name + ".spv");
			if (!File.Exists(path))
				continue;

			probed++;
			// Every one of these declares `computeMain` in Slang; resolution must land on whatever the module calls it.
			var resolved = SpirvEntryPoints.Resolve(
				File.ReadAllBytes(path), TechniqueDescriptor.DefaultComputeEntry, SpirvEntryPoints.Stage.Compute, name);
			if (resolved != "main")
				failures.Add($"{name}: resolved to '{resolved}', expected the renamed 'main'");
		}

		probed.ShouldBeGreaterThan(0, $"found no compute .spv to check in {shaders} — the guard would pass vacuously");
		failures.ShouldBeEmpty(string.Join("\n  ", failures));
	}
}
