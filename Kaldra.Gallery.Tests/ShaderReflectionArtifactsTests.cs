using BinaryVibrance.Kaldra.Graph;

namespace BinaryVibrance.Kaldra.Tests;

/// <summary>
/// Reading the reserved per-material parameter block, and each constant buffer's size, out of the gallery's real
/// <c>slangc -reflection-json</c> sidecars: what pins the parse against shipped artifacts (its unit tests, on hand-built
/// sidecars, are the engine's: ShaderReflectionMaterialTests).
/// </summary>
public class ShaderReflectionArtifactsTests
{
	[Test]
	public void The_real_mesh_sidecar_still_declares_no_material_block()
	{
		// Pins the reserved name against a shipped artifact: if a future shader edit named a block kaldraMaterial by
		// accident, or slangc stopped emitting parameter names, this notices.
		var path = Path.Combine(AppContext.BaseDirectory, "shaders", "mesh.json");
		if (!File.Exists(path))
			throw new TUnit.Core.Exceptions.SkipTestException($"reflection sidecar not found at {path}.");

		var reflection = ShaderReflection.FromBytes(File.ReadAllBytes(path), ShaderBindingTarget.SpirV);

		reflection.UniformBufferCount.ShouldBe(2); // view + draw — names are being read, so the parse is live
		reflection.HasMaterialParameters.ShouldBeFalse();
	}

	[Test]
	public void The_real_material_pbr_sidecar_reports_the_reserved_block()
	{
		// The end of the chain against a shipped artifact: material_pbr.slang declares a sampler then view, draw and
		// kaldraMaterial, so the block is descriptor 3 but uniform *slot* 2, and its three float4 rows lay out as 48
		// bytes. If slangc changed how it names or sizes the block, the parse would go quietly wrong; this notices.
		var path = Path.Combine(AppContext.BaseDirectory, "shaders", "material_pbr.json");
		if (!File.Exists(path))
			throw new TUnit.Core.Exceptions.SkipTestException($"reflection sidecar not found at {path}.");

		var reflection = ShaderReflection.FromBytes(File.ReadAllBytes(path), ShaderBindingTarget.SpirV);

		reflection.SampleCount.ShouldBe(1);
		reflection.UniformBufferCount.ShouldBe(3);
		reflection.MaterialUniformSlot.ShouldBe(2);
		reflection.MaterialParameterBytes.ShouldBe(48);
	}

	[Test]
	public void The_real_wide_push_shaders_declare_a_block_big_enough_for_what_the_demos_push()
	{
		// The guard on the push bound: a push routed to a constant buffer is checked against that buffer's declared
		// size, so under-reporting it would reject legitimate work. These two are the widest real pushes in the
		// gallery — CascadedShadowsDemo pushes 68 floats and the sky demos 36 — and both must fit.
		foreach (var (shader, slot, floats) in new[] { ("lighting_csm", 1, 68), ("sky", 0, 36) })
		{
			var path = Path.Combine(AppContext.BaseDirectory, "shaders", $"{shader}.json");
			if (!File.Exists(path))
				throw new TUnit.Core.Exceptions.SkipTestException($"reflection sidecar not found at {path}.");

			var reflection = ShaderReflection.FromBytes(File.ReadAllBytes(path), ShaderBindingTarget.SpirV);

			reflection.UniformSlotBytes(slot)
				.ShouldBeGreaterThanOrEqualTo(floats * sizeof(float), $"{shader} slot {slot} must hold the {floats} floats the demo pushes.");
		}
	}
}
