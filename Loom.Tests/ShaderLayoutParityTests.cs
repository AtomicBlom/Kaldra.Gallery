using Loom.Graph;

namespace Loom.Tests;

/// <summary>
/// The proof that reading real binding indices changes no shipped shader's descriptor layout.
/// <para>
/// Backends used to build a layout by assuming a category order — "N sampled textures, then M storage images, then
/// the storage buffers, then the constant buffers", numbered 0 upward — which every shader had to hand-number to
/// match. They now read each parameter's actual slot. For that to be a refactor rather than a behaviour change,
/// the two must agree on every artifact the repository ships, and this asserts exactly that against all of them.
/// </para>
/// <para>
/// It is a migration test with a finite life: once nothing derives a layout from counts, the thing it compares
/// against is gone and it should be deleted rather than maintained.
/// </para>
/// </summary>
public class ShaderLayoutParityTests
{
	[Test]
	public void Every_shipped_shader_derives_the_same_descriptor_layout_from_indices_as_from_category_counts()
	{
		var sidecars = ShippedSidecars();
		if (sidecars.Length == 0)
			throw new TUnit.Core.Exceptions.SkipTestException(
				$"no reflection sidecars found under {Path.Combine(AppContext.BaseDirectory, "shaders")} — build the gallery first.");

		var failures = new List<string>();
		var compared = 0;

		foreach (var path in sidecars)
		{
			var reflection = ShaderReflection.FromBytes(File.ReadAllBytes(path), ShaderBindingTarget.SpirV);
			if (reflection.Bindings.IsDefaultOrEmpty)
				continue; // a shader binding nothing has no layout to disagree about
			compared++;

			var name = Path.GetFileNameWithoutExtension(path);
			var fromIndices = string.Join(", ", reflection.Bindings.Select(b => $"{b.Index}:{Category(b.Kind)}x{b.SlotCount}"));
			var fromCounts = string.Join(", ", CategoryOrderLayout(reflection));

			if (fromIndices != fromCounts)
				failures.Add($"{name}: indices [{fromIndices}] but category order [{fromCounts}]");
		}

		// Guard against a vacuous pass: a glob that matched only shaders binding nothing would compare nothing.
		compared.ShouldBeGreaterThan(10, "too few shaders declared any binding — the sidecars are probably not the real ones.");
		failures.ShouldBeEmpty(
			"These shaders number their bindings differently from the category order the backends used to assume, so "
			+ "reading real indices changes their layout:\n  " + string.Join("\n  ", failures));
	}

	[Test]
	public void Every_shipped_shader_numbers_its_bindings_uniquely()
	{
		// Two parameters sharing a slot means one silently overwrites the other's descriptor. Nothing caught this
		// before: the old layout ignored the declared numbers entirely, so a duplicate was invisible until a shader
		// read the wrong resource. Deliberately NOT asserted here: category order, and gap-freedom — both were
		// requirements of the old scheme and reading real indices is what retires them.
		var sidecars = ShippedSidecars();
		if (sidecars.Length == 0)
			throw new TUnit.Core.Exceptions.SkipTestException("no reflection sidecars found — build the gallery first.");

		var failures = new List<string>();

		foreach (var path in sidecars)
		{
			var reflection = ShaderReflection.FromBytes(File.ReadAllBytes(path), ShaderBindingTarget.SpirV);
			var occupied = new Dictionary<int, string>();
			foreach (var binding in reflection.Bindings)
			{
				// A binding spanning several slots occupies every one of them.
				for (var slot = binding.Index; slot < binding.Index + binding.SlotCount; slot++)
				{
					if (occupied.TryGetValue(slot, out var existing))
						failures.Add($"{Path.GetFileNameWithoutExtension(path)}: slot {slot} claimed by both '{existing}' and '{binding.Name}'");
					else
						occupied[slot] = binding.Name;
				}
			}
		}

		failures.ShouldBeEmpty("Duplicate binding slots:\n  " + string.Join("\n  ", failures));
	}

	private static string[] ShippedSidecars()
	{
		var dir = Path.Combine(AppContext.BaseDirectory, "shaders");
		// The WGSL sidecars sit beside the SPIR-V ones under a .wgsl.json suffix and are a different index space.
		return Directory.Exists(dir)
			? [.. Directory.GetFiles(dir, "*.json").Where(p => !p.EndsWith(".wgsl.json", StringComparison.OrdinalIgnoreCase)).Order()]
			: [];
	}

	// The layout the backends used to build: every binding at a sequential slot from 0, grouped in category order,
	// one descriptor each. Spelled out here rather than called, because the production code no longer contains it —
	// this test is the only remaining witness to what it produced.
	private static IEnumerable<string> CategoryOrderLayout(ShaderReflection reflection)
	{
		var slot = 0;
		for (var i = 0; i < reflection.SampleCount; i++)
			yield return $"{slot++}:sampled x1";
		for (var i = 0; i < reflection.StorageImageCount; i++)
			yield return $"{slot++}:storage-image x1";
		for (var i = 0; i < reflection.StorageBufferCount; i++)
			yield return $"{slot++}:storage-buffer x1";
		for (var i = 0; i < reflection.UniformBufferCount; i++)
			yield return $"{slot++}:uniform x1";
	}

	private static string Category(ShaderBindingKind kind) => kind switch
	{
		ShaderBindingKind.SampledTexture => "sampled ",
		ShaderBindingKind.StorageImage => "storage-image ",
		ShaderBindingKind.StorageBufferReadOnly or ShaderBindingKind.StorageBufferReadWrite => "storage-buffer ",
		ShaderBindingKind.UniformBuffer => "uniform ",
		_ => throw new ArgumentOutOfRangeException(nameof(kind), kind, "Unmapped shader binding kind."),
	};
}
