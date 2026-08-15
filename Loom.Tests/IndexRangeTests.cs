using System.Numerics;

using Loom.Backend.DirectX12;
using Loom.Backend.Vulkan;
using Loom.Core.Geometry;
using Loom.Core.Graph;
using Loom.Core.Hosting;
using Loom.Core.Materials;
using Loom.Gallery.Demos;

namespace Loom.Tests;

/// <summary>
/// A draw can name a sub-range of a mesh's index buffer, so one mesh holds many primitives instead of becoming a mesh
/// each. Three numbers have to survive the trip through <see cref="DrawCall"/> into each backend's indexed-draw call,
/// and getting any of them wrong renders <em>something</em> — the wrong triangles, or the right triangles from the
/// wrong vertices — which is why this asserts which half of the screen is covered rather than that anything drew.
/// <para>
/// The mesh is two quads, left and right, and each number gets a draw that isolates it: a short count that must omit
/// the right quad, a first index that must skip the left one, and the left quad's own indices offset by a base vertex
/// so they land on the right quad. A backend that dropped any one of the three covers the wrong half.
/// </para>
/// GPU-gated — SKIPPED when no backend is available.
/// </summary>
[NotInParallel] // one GPU engine at a time
public class IndexRangeTests
{
	private const int W = 64, H = 64;

	[Test]
	public void A_range_draws_only_its_own_part_of_the_mesh()
	{
		foreach (var backend in AvailableBackends())
		{
			// Each of the three numbers gets its own draw, so a backend that dropped one is named by the failure
			// rather than lumped in with the others.
			var whole = Render(backend, default);
			var byCount = Render(backend, DrawCall.Range(0, 6));
			var byFirstIndex = Render(backend, DrawCall.Range(6, 6));
			var byBaseVertex = Render(backend, DrawCall.Range(0, 6, baseVertex: 4));

			whole.Left.ShouldBeTrue($"{backend.Name}: the default range should draw the whole mesh, but the left quad is missing.");
			whole.Right.ShouldBeTrue($"{backend.Name}: the default range should draw the whole mesh, but the right quad is missing.");

			byCount.Left.ShouldBeTrue($"{backend.Name}: a range of the first six indices should draw the left quad.");
			byCount.Right.ShouldBeFalse($"{backend.Name}: a range of the first six indices drew the right quad too — indexCount was ignored.");

			byFirstIndex.Right.ShouldBeTrue($"{backend.Name}: a range starting at index 6 should draw the right quad — firstIndex was ignored.");
			byFirstIndex.Left.ShouldBeFalse($"{backend.Name}: a range starting at index 6 drew the left quad — firstIndex was ignored.");

			byBaseVertex.Right.ShouldBeTrue($"{backend.Name}: the left quad's indices offset by baseVertex 4 should draw the right quad — baseVertex was ignored.");
			byBaseVertex.Left.ShouldBeFalse($"{backend.Name}: the left quad's indices offset by baseVertex 4 still drew the left quad — baseVertex was ignored.");
		}
	}

	[Test]
	public void A_range_past_the_end_of_the_mesh_is_refused()
	{
		foreach (var backend in AvailableBackends())
		{
			// Reading past the index buffer is undefined behaviour that no backend reports — garbage triangles on one
			// driver, a fault on another. Both backends check it against the mesh they own.
			var thrown = Should.Throw<Exception>(() => Render(backend, DrawCall.Range(6, 12)));
			thrown.Message.ShouldContain("18", customMessage: $"{backend.Name}: the message should name the end of the range that overran.");
		}
	}

	[Test]
	public void A_range_rejects_negative_components()
	{
		// Device-free: a negative first index or count is a caller mistake, caught where both numbers are named
		// rather than after they have been cast to unsigned and become enormous.
		Should.Throw<ArgumentOutOfRangeException>(() => DrawCall.Range(-1, 6));
		Should.Throw<ArgumentOutOfRangeException>(() => DrawCall.Range(0, -6));
		Should.Throw<ArgumentOutOfRangeException>(() => DrawCall.Range(0, 6, baseVertex: -4));
	}

	[Test]
	public void The_default_range_means_the_whole_mesh()
	{
		// The property every existing draw relies on: a DrawCall that never mentions a range must behave exactly as
		// it did before ranges existed.
		default(DrawCall.IndexRange).IsEmpty.ShouldBeTrue();
		DrawCall.Range(0, 6).IsEmpty.ShouldBeFalse();
	}

	private static List<IRenderBackend> AvailableBackends()
	{
		_ = new VulkanEngineSettings();
		_ = new DirectX12EngineSettings();
		if (Backends.Available.Count == 0)
			throw new TUnit.Core.Exceptions.SkipTestException("No GPU backend is available on this box (Backends.Available is empty).");
		return [.. Backends.Available];
	}

	// Two quads either side of the origin, sharing one vertex and index buffer — the shape a glTF model's primitives
	// take once they stop being a mesh each.
	private static (Vertex[] Vertices, uint[] Indices) TwoQuads()
	{
		var n = new Vector3(0, 0, 1);
		var tan = new Vector4(1, 0, 0, 1);
		Vertex[] vertices =
		[
			new(new Vector3(-0.9f, -0.5f, 0), n, new Vector2(0, 0), tan),
			new(new Vector3(-0.1f, -0.5f, 0), n, new Vector2(1, 0), tan),
			new(new Vector3(-0.1f, 0.5f, 0), n, new Vector2(1, 1), tan),
			new(new Vector3(-0.9f, 0.5f, 0), n, new Vector2(0, 1), tan),

			new(new Vector3(0.1f, -0.5f, 0), n, new Vector2(0, 0), tan),
			new(new Vector3(0.9f, -0.5f, 0), n, new Vector2(1, 0), tan),
			new(new Vector3(0.9f, 0.5f, 0), n, new Vector2(1, 1), tan),
			new(new Vector3(0.1f, 0.5f, 0), n, new Vector2(0, 1), tan),
		];
		return (vertices, [0, 1, 2, 0, 2, 3, 4, 5, 6, 4, 6, 7]);
	}

	// Render the two-quad mesh through one range and report which halves of the frame ended up covered.
	private static (bool Left, bool Right) Render(IRenderBackend backend, DrawCall.IndexRange range)
	{
		using var engine = backend.Create(Surface.Offscreen(W, H), W, H, new EngineSettings());
		engine.DefineTechnique("material.unlit", new TechniqueDescriptor("material_unlit", HasVertexInput: true));

		var (vertices, indices) = TwoQuads();
		var mesh = engine.CreateMesh(vertices, indices);
		var tint = new Vector4(1f, 1f, 1f, 1f);
		var material = engine.CreateMaterial("material.unlit", in tint);

		ConstantsHandle viewproj = null!;
		engine.SetGraph(RenderGraph.Define(g =>
		{
			var screen = g.Backbuffer;
			var depth = g.Screen.Depth("depth", Format.Depth32F);
			viewproj = g.Constants<Matrix4x4>("range.viewproj");

			g.Pass("scene")
				.Reads(viewproj)
				.Writes(screen)
				.WritesDepth(depth)
				.Draw(ctx =>
				{
					var block = new PerDraw { Model = Matrix4x4.Identity, ObjectId = 0 };
					var call = new DrawCall { Mesh = mesh, Technique = "material.unlit", Material = material, Indices = range };
					call.SetUniforms(ref block);
					ctx.Draw(call);
				});
			g.Present(screen);
		}));

		var camera = new Loom.Concepts.Camera(engine)
			.Perspective(0.9f, (float)W / H, 0.1f, 100f)
			.LookAt(new Vector3(0, 0, 2.2f), Vector3.Zero);
		engine.Write(viewproj, camera.ViewProjection);

		var path = Path.Combine(Path.GetTempPath(), $"loom-range-{Guid.NewGuid():N}.png");
		try
		{
			var inputs = new FrameInputs { Time = 0, DeltaSeconds = 1.0 / 60.0 };
			engine.Render(inputs);
			engine.RequestCapture(path);
			for (var frame = 0; frame < 3; frame++)
				engine.Render(inputs);

			var (width, _, rgba) = PngReader.Read(path);
			return (Covered(rgba, width, W / 4, H / 2), Covered(rgba, width, W * 3 / 4, H / 2));
		}
		finally
		{
			if (File.Exists(path))
				File.Delete(path);
		}
	}

	// The quads are drawn pure white on a cleared background, so "covered" is unambiguous rather than a tolerance.
	private static bool Covered(byte[] rgba, int width, int x, int y)
	{
		var i = ((y * width) + x) * 4;
		return rgba[i] > 200 && rgba[i + 1] > 200 && rgba[i + 2] > 200;
	}
}
