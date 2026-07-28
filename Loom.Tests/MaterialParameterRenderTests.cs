using System.Numerics;
using System.Runtime.InteropServices;

using Loom.Backend.DirectX12;
using Loom.Backend.Vulkan;
using Loom.Concepts;
using Loom.Core.Geometry;
using Loom.Core.Graph;
using Loom.Core.Hosting;
using Loom.Core.Materials;
using Loom.Gallery.Demos;

namespace Loom.Tests;

/// <summary>
/// The per-material parameter channel, on the GPU: a material's parameter block actually reaches the shader that
/// declares <c>loomMaterial</c>, and rewriting it with <c>IEngine.Write(material, …)</c> changes the next frame. This
/// is the half a CPU test can't reach — the block is identified by reflection, bound at whatever uniform slot
/// reflection reports, and staged through each backend's per-frame uniform ring, so nothing but a render proves the
/// three agree. GPU-gated; runs on each available backend independently.
/// </summary>
[NotInParallel] // one GPU engine at a time
public class MaterialParameterRenderTests
{
	private const int W = 128, H = 128;

	// The shader-side LoomMaterial in material_pbr.slang: three float4 rows, every member 16-byte aligned so HLSL
	// cbuffer packing and WGSL uniform layout agree without a per-backend branch.
	[StructLayout(LayoutKind.Sequential)]
	private readonly record struct PbrFactors(Vector4 BaseColor, Vector4 Emissive, Vector4 MetallicRoughness);

	[Test]
	public void A_materials_parameter_block_reaches_the_shader_that_declares_it()
	{
		foreach (var backend in AvailableBackends())
		{
			// Emissive is added unlit, so it isolates the block from every lighting term: a red-emissive material and
			// a green-emissive one must differ in exactly the channels the block says, on every backend.
			var red = RenderQuad(backend, Emissive(1f, 0f, 0f));
			var green = RenderQuad(backend, Emissive(0f, 1f, 0f));

			red.R.ShouldBeGreaterThan(160, $"{backend.Name}: the red material's emissive did not reach the shader (got {red}).");
			red.G.ShouldBeLessThan(120, $"{backend.Name}: the red material leaked green (got {red}).");
			green.G.ShouldBeGreaterThan(160, $"{backend.Name}: the green material's emissive did not reach the shader (got {green}).");
			green.R.ShouldBeLessThan(120, $"{backend.Name}: the green material leaked red (got {green}).");
		}
	}

	[Test]
	public void Rewriting_a_materials_parameters_changes_the_next_frame()
	{
		// The reason the block is staged per draw rather than uploaded once: engine.Write(material, …) has to take
		// effect with no graph rebuild. Both frames come from ONE engine and ONE graph, so a difference can only be
		// the rewritten block.
		foreach (var backend in AvailableBackends())
		{
			var (before, after) = RenderQuadThenRewrite(backend, Emissive(1f, 0f, 0f), Emissive(0f, 0f, 1f));

			before.R.ShouldBeGreaterThan(160, $"{backend.Name}: the initial block did not reach the shader (got {before}).");
			after.B.ShouldBeGreaterThan(160, $"{backend.Name}: the rewritten block did not reach the shader (got {after}).");
			after.R.ShouldBeLessThan(120, $"{backend.Name}: the rewritten block kept the old red (got {after}).");
		}
	}

	[Test]
	public void The_same_block_renders_identically_on_every_backend()
	{
		// Cross-backend parity for the channel itself: the same 48 bytes, the same slot, the same pixels. Catches a
		// packing or slot-order divergence that each backend would otherwise render self-consistently but differently.
		var backends = AvailableBackends();
		if (backends.Count < 2)
			throw new TUnit.Core.Exceptions.SkipTestException("cross-backend parity needs two backends on this box.");

		var factors = new PbrFactors(
			new Vector4(0.8f, 0.35f, 0.15f, 1f),
			new Vector4(0.05f, 0.02f, 0.2f, 0f),
			new Vector4(0.6f, 0.3f, 0f, 0f));

		var samples = backends.Select(b => (b.Name, Colour: RenderQuad(b, factors))).ToArray();
		for (var i = 1; i < samples.Length; i++)
		{
			var delta = Math.Max(Math.Max(
				Math.Abs(samples[i].Colour.R - samples[0].Colour.R),
				Math.Abs(samples[i].Colour.G - samples[0].Colour.G)),
				Math.Abs(samples[i].Colour.B - samples[0].Colour.B));
			delta.ShouldBeLessThanOrEqualTo(4,
				$"{samples[0].Name} rendered {samples[0].Colour} but {samples[i].Name} rendered {samples[i].Colour} from the same material block.");
		}
	}

	[Test]
	public void A_block_declared_before_the_other_uniforms_binds_at_its_own_slot()
	{
		// material_unlit declares loomMaterial FIRST, so it is uniform slot 0 while the view-projection is 1 and the
		// per-draw block is 2. In every other material shader the block is declared last, where "bind it at the
		// reflected slot" and "append it after everything else" coincide — a backend that appended would still look
		// right. Here appending would land the block at slot 2 and the shader would read a view-projection as its tint.
		foreach (var backend in AvailableBackends())
		{
			var tint = new Vector4(0.2f, 0.75f, 0.35f, 1f);
			var pixel = RenderUnlitQuad(backend, tint);

			// The tint arrives through an Rgba8Srgb backbuffer, so compare against the sRGB-encoded value rather than
			// the linear one — the point is which slot was read, and a wrong slot gives black or noise, not a near miss.
			var expected = new Rgb(Srgb8(tint.X), Srgb8(tint.Y), Srgb8(tint.Z));
			var delta = Math.Max(Math.Max(Math.Abs(pixel.R - expected.R), Math.Abs(pixel.G - expected.G)), Math.Abs(pixel.B - expected.B));
			delta.ShouldBeLessThanOrEqualTo(4,
				$"{backend.Name}: expected {expected} from the slot-0 material block but got {pixel} — the block was read from the wrong uniform slot.");
		}
	}

	private static int Srgb8(float linear)
	{
		var encoded = linear <= 0.0031308f ? linear * 12.92f : (1.055f * MathF.Pow(linear, 1f / 2.4f)) - 0.055f;
		return (int)MathF.Round(Math.Clamp(encoded, 0f, 1f) * 255f);
	}

	private static Rgb RenderUnlitQuad(IRenderBackend backend, Vector4 tint)
	{
		using var engine = backend.Create(Surface.Offscreen(W, H), W, H, new EngineSettings());
		engine.DefineTechnique("material.unlit", new TechniqueDescriptor("material_unlit", HasVertexInput: true));

		// A 16-byte block and no textures — a different shape from material_pbr's, so the staged size comes from this
		// shader's own reflection.
		var material = engine.CreateMaterial("material.unlit", in tint);
		var quad = Quad(engine);
		var camera = new Camera(engine).Perspective(0.9f, 1f, 0.1f, 100f).LookAt(new Vector3(0, 0, 3), Vector3.Zero);

		ConstantsHandle viewproj = null!;
		var graph = RenderGraph.Define(g =>
		{
			var screen = g.Backbuffer;
			var depth = g.Screen.Depth("depth", Format.Depth32F);
			viewproj = g.Constants<Matrix4x4>("viewproj");
			g.Pass("scene")
				.Reads(viewproj)
				.Writes(screen)
				.WritesDepth(depth)
				.Draw(ctx => DemoDraw.Mesh(ctx, quad, "material.unlit", Matrix4x4.Identity, 0, material));
			g.Present(screen);
		});
		engine.SetGraph(graph);
		engine.Write(viewproj, camera.ViewProjection);

		return CaptureCentre(engine, new FrameInputs { Time = 0, DeltaSeconds = 1.0 / 60.0 });
	}

	private static List<IRenderBackend> AvailableBackends()
	{
		_ = new VulkanEngineSettings();
		_ = new DirectX12EngineSettings();
		if (Backends.Available.Count == 0)
			throw new TUnit.Core.Exceptions.SkipTestException("no GPU backend available on this box.");
		return [.. Backends.Available];
	}

	// A material whose only contribution is an unlit emissive colour — base colour black so no lighting term can
	// muddy the reading, roughness 1 so the specular lobe collapses.
	private static PbrFactors Emissive(float r, float g, float b) => new(
		BaseColor: new Vector4(0f, 0f, 0f, 1f),
		Emissive: new Vector4(r, g, b, 0f),
		MetallicRoughness: new Vector4(0f, 1f, 0f, 0f));

	private static Rgb RenderQuad(IRenderBackend backend, PbrFactors factors)
	{
		var (only, _) = Render(backend, factors, rewriteTo: null);
		return only;
	}

	private static (Rgb Before, Rgb After) RenderQuadThenRewrite(IRenderBackend backend, PbrFactors initial, PbrFactors rewritten) =>
		Render(backend, initial, rewritten);

	// Draw one screen-filling quad with material_pbr and read the centre pixel. When `rewriteTo` is supplied, the same
	// engine and graph render a second frame after engine.Write(material, …) — proving mutation needs no rebuild.
	private static (Rgb First, Rgb Second) Render(IRenderBackend backend, PbrFactors factors, PbrFactors? rewriteTo)
	{
		using var engine = backend.Create(Surface.Offscreen(W, H), W, H, new EngineSettings());
		engine.DefineTechnique("material.pbr", new TechniqueDescriptor("material_pbr", HasVertexInput: true));

		// A white 1x1 albedo, so base colour comes from the factor alone — glTF's "texture absent => the factor
		// applies unmodified", and the same fallback MaterialUploader will use.
		var white = engine.CreateTexture(TextureData.FromRgba8(1, 1, [255, 255, 255, 255], TextureFormat.Rgba8Srgb, generateMips: false));
		var material = engine.CreateMaterial("material.pbr", in factors, white);
		var quad = Quad(engine);
		var camera = new Camera(engine).Perspective(0.9f, 1f, 0.1f, 100f).LookAt(new Vector3(0, 0, 3), Vector3.Zero);

		ConstantsHandle viewproj = null!;
		var graph = RenderGraph.Define(g =>
		{
			var screen = g.Backbuffer;
			var depth = g.Screen.Depth("depth", Format.Depth32F);
			viewproj = g.Constants<Matrix4x4>("viewproj");
			g.Pass("scene")
				.Reads(viewproj)
				.Writes(screen)
				.WritesDepth(depth)
				.Draw(ctx => DemoDraw.Mesh(ctx, quad, "material.pbr", Matrix4x4.Identity, 0, material));
			g.Present(screen);
		});
		engine.SetGraph(graph);
		engine.Write(viewproj, camera.ViewProjection);

		var inputs = new FrameInputs { Time = 0, DeltaSeconds = 1.0 / 60.0 };
		var first = CaptureCentre(engine, inputs);
		if (rewriteTo is not { } next)
			return (first, default);

		engine.Write(material, in next);
		return (first, CaptureCentre(engine, inputs));
	}

	private static Rgb CaptureCentre(IEngine engine, FrameInputs inputs)
	{
		var path = Path.Combine(Path.GetTempPath(), $"loom-matparam-{Guid.NewGuid():N}.png");
		try
		{
			engine.RequestCapture(path);
			engine.Render(inputs);
			var (w, h, rgba) = PngReader.Read(path);
			var i = ((h / 2) * w + (w / 2)) * 4;
			return new Rgb(rgba[i], rgba[i + 1], rgba[i + 2]);
		}
		finally
		{
			if (File.Exists(path)) File.Delete(path);
		}
	}

	private static Mesh Quad(IEngine engine)
	{
		// Facing the camera (+Z normal) so the key light lands consistently; big enough to cover the centre pixel.
		var normal = new Vector3(0, 0, 1);
		var tangent = new Vector4(1, 0, 0, 1);
		Span<Vertex> vertices =
		[
			new(new Vector3(-1.5f, -1.5f, 0), normal, new Vector2(0, 0), tangent),
			new(new Vector3(1.5f, -1.5f, 0), normal, new Vector2(1, 0), tangent),
			new(new Vector3(1.5f, 1.5f, 0), normal, new Vector2(1, 1), tangent),
			new(new Vector3(-1.5f, 1.5f, 0), normal, new Vector2(0, 1), tangent),
		];
		Span<uint> indices = [0, 1, 2, 0, 2, 3];
		return engine.CreateMesh(vertices, indices);
	}

	private readonly record struct Rgb(int R, int G, int B)
	{
		public override string ToString() => $"rgb({R},{G},{B})";
	}
}
