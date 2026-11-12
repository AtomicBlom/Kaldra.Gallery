using Loom.Backend.DirectX12;
using Loom.Graph;

namespace Loom.Tests;

/// <summary>
/// DirectX 12's root signatures are built at the registers Slang gave each shader's parameters (DirectX12Registers),
/// since shaders name none. These resolve every shipped shader's DXIL sidecar against its SPIR-V one, as the backend
/// does when it registers a technique, and hold the result to what a root signature needs.
/// </summary>
public class DirectX12RegistersTests
{
	[Test]
	public void Every_shipped_shader_resolves_a_register_for_each_binding_with_none_shared()
	{
		var dir = Path.Combine(AppContext.BaseDirectory, "shaders");
		var sidecars = Directory.Exists(dir) ? Directory.GetFiles(dir, "*.dxil.json").Order().ToArray() : [];
		if (sidecars.Length == 0)
			throw new TUnit.Core.Exceptions.SkipTestException($"no DXIL reflection sidecars under {dir} — build the gallery with DirectX 12 first.");

		var failures = new List<string>();
		var resolved = 0;
		foreach (var path in sidecars)
		{
			var name = Path.GetFileName(path)[..^".dxil.json".Length];
			var spirv = Path.Combine(dir, name + ".json");
			if (!File.Exists(spirv))
			{
				failures.Add($"{name}: a DXIL sidecar with no SPIR-V one");
				continue;
			}
			var map = ShaderBindingMap.From(ShaderReflection.FromBytes(File.ReadAllBytes(spirv), ShaderBindingTarget.SpirV));
			DirectX12Registers registers;
			try
			{
				registers = DirectX12Registers.Resolve(File.ReadAllBytes(path), map, name);
			}
			catch (Exception e)
			{
				failures.Add($"{name}: {e.Message}");
				continue;
			}
			resolved++;

			// One register per binding, in the map's order, so the k-th root parameter is the k-th binding's.
			if (registers.Textures.Length != map.SampledTextures.Length || registers.Samplers.Length != map.SampledTextures.Length)
				failures.Add($"{name}: {map.SampledTextures.Length} textures but {registers.Textures.Length} t and {registers.Samplers.Length} s registers");
			if (registers.UniformBuffers.Length != map.UniformBuffers.Length)
				failures.Add($"{name}: {map.UniformBuffers.Length} uniform buffers but {registers.UniformBuffers.Length} b registers");

			// Root parameters of one register kind must not overlap: the root signature would fail to serialize.
			Distinct(name, 't', [.. registers.Textures, .. registers.InputBuffers], failures);
			Distinct(name, 's', [.. registers.Samplers], failures);
			Distinct(name, 'b', [.. registers.UniformBuffers, registers.Push], failures);
			Distinct(name, 'u', [.. registers.StorageImages, .. registers.ReadWriteBuffers], failures);
		}

		resolved.ShouldBeGreaterThan(10, "too few shaders resolved — the sidecars are probably not the real ones.");
		failures.ShouldBeEmpty("DXIL registers that won't build a root signature:\n  " + string.Join("\n  ", failures));
	}

	[Test]
	public void The_push_block_is_the_constant_buffer_the_SPIR_V_reflection_does_not_list()
	{
		// As D3D lays it out: a push block declared between two constant buffers takes the b register between theirs.
		var dxil = """
			{ "parameters": [
				{ "name": "view", "binding": { "kind": "constantBuffer", "index": 0 } },
				{ "name": "pc", "binding": { "kind": "constantBuffer", "index": 1 } },
				{ "name": "draw", "binding": { "kind": "constantBuffer", "index": 2 } },
				{ "name": "albedo", "bindings": [ { "kind": "shaderResource", "index": 0 }, { "kind": "samplerState", "index": 0 } ] }
			] }
			"""u8.ToArray();
		var map = ShaderBindingMap.From(ShaderReflection.FromBindings(
			[
				new ShaderBinding("albedo", 0, ShaderBindingKind.SampledTexture),
				new ShaderBinding("view", 1, ShaderBindingKind.UniformBuffer),
				new ShaderBinding("draw", 2, ShaderBindingKind.UniformBuffer),
			], ShaderBindingTarget.SpirV));

		var registers = DirectX12Registers.Resolve(dxil, map, "test");

		registers.Push.ShouldBe(1);
		registers.UniformBuffers.ShouldBe([0, 2]);
		registers.Textures.ShouldBe([0]);
		registers.Samplers.ShouldBe([0]);
	}

	[Test]
	public void Root_constants_with_no_push_block_go_after_the_last_constant_buffer()
	{
		var dxil = """{ "parameters": [ { "name": "view", "binding": { "kind": "constantBuffer", "index": 0 } } ] }"""u8.ToArray();
		var map = ShaderBindingMap.From(ShaderReflection.FromBindings(
			[new ShaderBinding("view", 0, ShaderBindingKind.UniformBuffer)], ShaderBindingTarget.SpirV));

		DirectX12Registers.Resolve(dxil, map, "test").Push.ShouldBe(1);
	}

	private static void Distinct(string shader, char kind, int[] registers, List<string> failures)
	{
		foreach (var shared in registers.GroupBy(r => r).Where(g => g.Count() > 1))
			failures.Add($"{shader}: {kind}{shared.Key} given to {shared.Count()} root parameters");
	}
}
