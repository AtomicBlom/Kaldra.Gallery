using System.Numerics;

using Loom.Backend.DirectX12;
using Loom.Backend.Vulkan;
using Loom.Core.Graph;
using Loom.Core.Hosting;
using Loom.Core.Materials;
using Loom.Gallery.Demos;

namespace Loom.Tests;

/// <summary>
/// A Constants or Storage buffer is uploaded only into the frame slots behind its last <see cref="IEngine.Write"/>, not
/// every frame — and that must change nothing a frame shows: a value written once is what every later frame reads, in
/// every slot; a new write reaches every slot; and a resize, which may rebuild the buffers, uploads them again. Each
/// frame is captured, so a slot left holding an older value is seen in the frame that reads it. GPU-gated — SKIPPED when
/// no backend is available.
/// </summary>
[NotInParallel] // one GPU engine at a time
public class ShadowUploadTests
{
	private const int W = 64, H = 64;
	private const int FramesChecked = 4;   // more than any backend has slots

	[Test]
	public void Every_frame_reads_the_last_write_though_only_stale_slots_are_uploaded()
	{
		foreach (var backend in AvailableBackends())
		{
			using var engine = backend.Create(Surface.Offscreen(W, H), W, H, new EngineSettings());
			engine.DefineTechnique("material.unlit", new TechniqueDescriptor("material_unlit", HasVertexInput: true));
			var (vertices, indices) = IndexRangeTests.TwoQuads();
			var mesh = engine.CreateMesh(vertices, indices);
			var tint = new Vector4(1f, 1f, 1f, 1f);
			var material = engine.CreateMaterial("material.unlit", in tint);

			ConstantsHandle viewproj = null!;
			engine.SetGraph(RenderGraph.Define(g =>
			{
				var screen = g.Backbuffer;
				var depth = g.Screen.Depth("depth", Format.Depth32F);
				viewproj = g.Constants<Matrix4x4>("upload.viewproj");
				g.Pass("scene")
					.Reads(viewproj)
					.Writes(screen)
					.WritesDepth(depth)
					.Draw(ctx =>
					{
						var block = new PerDraw { Model = Matrix4x4.Identity, ObjectId = 0 };
						var call = new DrawCall { Mesh = mesh, Technique = "material.unlit", Material = material };
						call.SetUniforms(ref block);
						ctx.Draw(call);
					});
				g.Present(screen);
			}));

			Matrix4x4 Looking(Vector3 at) => new Loom.Concepts.Camera(engine)
				.Perspective(0.9f, (float)W / H, 0.1f, 100f)
				.LookAt(new Vector3(0, 0, 2.2f), at).ViewProjection;
			var atQuads = Looking(Vector3.Zero);
			var away = Looking(new Vector3(0, 0, 10f));   // facing away: nothing in view

			engine.Write(viewproj, atQuads);
			Frames(engine, backend, true, "written once, read in every slot");
			engine.Write(viewproj, away);
			Frames(engine, backend, false, "written again, every slot caught up");
			engine.Write(viewproj, atQuads);
			Frames(engine, backend, true, "and back");
			engine.Resize(W, H);
			Frames(engine, backend, true, "after a resize rebuilt what it rebuilds");
		}
	}

	// Each of several frames captured in turn, each showing the quads or not
	private static void Frames(IEngine engine, IRenderBackend backend, bool expectQuads, string step)
	{
		var inputs = new FrameInputs { Time = 0, DeltaSeconds = 1.0 / 60.0 };
		var paths = Enumerable.Range(0, FramesChecked).Select(_ => Path.Combine(Path.GetTempPath(), $"loom-upload-{Guid.NewGuid():N}.png")).ToList();
		try
		{
			foreach (var path in paths)
			{
				engine.RequestCapture(path);
				engine.Render(inputs);
			}
			for (var flush = 0; flush < 3; flush++)
				engine.Render(inputs);
			for (var i = 0; i < paths.Count; i++)
			{
				var (width, _, rgba) = PngReader.Read(paths[i]);
				var covered = Covered(rgba, width, W / 4, H / 2) && Covered(rgba, width, W * 3 / 4, H / 2);
				covered.ShouldBe(expectQuads, $"{backend.Name}, {step}: frame {i} {(expectQuads ? "lost the write" : "still read an older write")}.");
			}
		}
		finally
		{
			foreach (var path in paths.Where(File.Exists))
				File.Delete(path);
		}
	}

	private static bool Covered(byte[] rgba, int width, int x, int y)
	{
		var i = ((y * width) + x) * 4;
		return rgba[i] > 200 && rgba[i + 1] > 200 && rgba[i + 2] > 200;
	}

	private static List<IRenderBackend> AvailableBackends()
	{
		_ = new VulkanEngineSettings();
		_ = new DirectX12EngineSettings();
		if (Backends.Available.Count == 0)
			throw new TUnit.Core.Exceptions.SkipTestException("No GPU backend is available on this box (Backends.Available is empty).");
		return [.. Backends.Available];
	}
}
