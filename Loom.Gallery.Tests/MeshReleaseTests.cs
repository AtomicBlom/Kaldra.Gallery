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
/// <see cref="IEngine.ReleaseMesh"/> lets geometry live shorter than the engine — a generated world replaced each
/// round, a streamed chunk. Two things can go wrong, and neither is visible in a single frame: the buffers are freed
/// while a frame in flight still reads them (a device fault, or garbage triangles, a frame or two later), or the
/// stale handle keeps resolving and draws whatever took its memory. So this swaps meshes repeatedly across frames —
/// including releasing one before any frame has drawn it, while its upload may still be pending — and asserts the
/// frame shows the replacement and only the replacement, then that a stale handle is refused by name.
/// GPU-gated — SKIPPED when no backend is available.
/// </summary>
[NotInParallel] // one GPU engine at a time
public class MeshReleaseTests
{
	private const int W = 64, H = 64;

	[Test]
	public void A_released_mesh_stops_drawing_and_its_replacement_draws()
	{
		foreach (var backend in AvailableBackends())
		{
			using var scene = new Scene(backend);

			// Churn: each round replaces the mesh the pass draws and renders a frame or two, so releases land while
			// earlier frames are still in flight. Every third round releases a mesh nothing has drawn yet.
			var current = CreateQuad(scene.Engine, left: true);
			for (var round = 0; round < 12; round++)
			{
				scene.Drawn = current;
				scene.Render(frames: 1 + round % 2);

				if (round % 3 == 0)
					scene.Engine.ReleaseMesh(CreateQuad(scene.Engine, left: false));

				var next = CreateQuad(scene.Engine, left: round % 2 == 1);
				scene.Drawn = next;
				scene.Engine.ReleaseMesh(current);
				current = next;
			}

			var final = CreateQuad(scene.Engine, left: false);
			scene.Drawn = final;
			scene.Engine.ReleaseMesh(current);
			var (left, right) = scene.Capture();

			right.ShouldBeTrue($"{backend.Name}: the replacement mesh (the right quad) should be drawn.");
			left.ShouldBeFalse($"{backend.Name}: the released mesh (the left quad) is still being drawn.");
		}
	}

	[Test]
	public void A_released_handle_is_refused()
	{
		foreach (var backend in AvailableBackends())
		{
			using var scene = new Scene(backend);
			var mesh = CreateQuad(scene.Engine, left: true);
			scene.Drawn = mesh;
			scene.Render(frames: 2);

			scene.Engine.ReleaseMesh(mesh);

			// Drawing it would read memory the engine is about to free, or already has; releasing it twice would free
			// it twice. Both are the caller holding a dead handle, so both name it.
			var drawn = Should.Throw<Exception>(() => scene.Render(frames: 1));
			drawn.ToString().ShouldContain("released", customMessage: $"{backend.Name}: drawing a released mesh should say it was released.");
			var twice = Should.Throw<Exception>(() => scene.Engine.ReleaseMesh(mesh));
			twice.Message.ShouldContain("released", customMessage: $"{backend.Name}: releasing a mesh twice should say it was released.");
		}
	}

	private static List<IRenderBackend> AvailableBackends()
	{
		_ = new VulkanEngineSettings();
		_ = new DirectX12EngineSettings();
		if (Backends.Available.Count == 0)
			throw new TUnit.Core.Exceptions.SkipTestException("No GPU backend is available on this box (Backends.Available is empty).");
		return [.. Backends.Available];
	}

	// One white quad, on the left or the right half of the frame.
	private static Mesh CreateQuad(IEngine engine, bool left)
	{
		var (x0, x1) = left ? (-0.9f, -0.1f) : (0.1f, 0.9f);
		var n = new Vector3(0, 0, 1);
		var tan = new Vector4(1, 0, 0, 1);
		Vertex[] vertices =
		[
			new(new Vector3(x0, -0.5f, 0), n, new Vector2(0, 0), tan),
			new(new Vector3(x1, -0.5f, 0), n, new Vector2(1, 0), tan),
			new(new Vector3(x1, 0.5f, 0), n, new Vector2(1, 1), tan),
			new(new Vector3(x0, 0.5f, 0), n, new Vector2(0, 1), tan),
		];
		return engine.CreateMesh(vertices, [0, 1, 2, 0, 2, 3]);
	}

	// An offscreen engine whose one pass draws whichever mesh Drawn names this frame — the pass reads it live, the
	// way a game's renderer holds the world it currently shows.
	private sealed class Scene : IDisposable
	{
		public IEngine Engine { get; }
		public Mesh Drawn { get; set; }

		public Scene(IRenderBackend backend)
		{
			Engine = backend.Create(Surface.Offscreen(W, H), W, H, new EngineSettings());
			Engine.DefineTechnique("material.unlit", new TechniqueDescriptor("material_unlit", HasVertexInput: true));
			var tint = new Vector4(1f, 1f, 1f, 1f);
			var material = Engine.CreateMaterial("material.unlit", in tint);

			ConstantsHandle viewproj = null!;
			Engine.SetGraph(RenderGraph.Define(g =>
			{
				var screen = g.Backbuffer;
				viewproj = g.Constants<Matrix4x4>("release.viewproj");
				g.Pass("scene")
					.Reads(viewproj)
					.Writes(screen)
					.Draw(this, (ctx, scene) =>
					{
						var block = new PerDraw { Model = Matrix4x4.Identity, ObjectId = 0 };
						var call = new DrawCall { Mesh = scene.Drawn, Technique = "material.unlit", Material = material };
						call.SetUniforms(ref block);
						ctx.Draw(call);
					});
				g.Present(screen);
			}));

			var camera = new Loom.Concepts.Camera(Engine)
				.Perspective(0.9f, (float)W / H, 0.1f, 100f)
				.LookAt(new Vector3(0, 0, 2.2f), Vector3.Zero);
			Engine.Write(viewproj, camera.ViewProjection);
		}

		public void Render(int frames)
		{
			var inputs = new FrameInputs { Time = 0, DeltaSeconds = 1.0 / 60.0 };
			for (var frame = 0; frame < frames; frame++)
				Engine.Render(inputs);
		}

		// Which halves of the frame are covered, from a capture of the next frame.
		public (bool Left, bool Right) Capture()
		{
			var path = Path.Combine(Path.GetTempPath(), $"loom-release-{Guid.NewGuid():N}.png");
			try
			{
				Engine.RequestCapture(path);
				Render(frames: 3);
				var (width, _, rgba) = PngReader.Read(path);
				return (Covered(rgba, width, W / 4, H / 2), Covered(rgba, width, W * 3 / 4, H / 2));
			}
			finally
			{
				if (File.Exists(path))
					File.Delete(path);
			}
		}

		public void Dispose() => Engine.Dispose();

		// The quads are pure white on a cleared background, so "covered" is unambiguous rather than a tolerance.
		private static bool Covered(byte[] rgba, int width, int x, int y)
		{
			var i = ((y * width) + x) * 4;
			return rgba[i] > 200 && rgba[i + 1] > 200 && rgba[i + 2] > 200;
		}
	}
}
