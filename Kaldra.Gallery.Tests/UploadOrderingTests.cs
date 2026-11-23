using System.Numerics;

using BinaryVibrance.Kaldra.Backend.DirectX12;
using BinaryVibrance.Kaldra.Backend.Vulkan;
using BinaryVibrance.Kaldra.Core.Geometry;
using BinaryVibrance.Kaldra.Core.Graph;
using BinaryVibrance.Kaldra.Core.Hosting;
using BinaryVibrance.Kaldra.Core.Materials;
using Kaldra.Gallery.Demos;

namespace BinaryVibrance.Kaldra.Tests;

/// <summary>
/// Resource uploads are recorded into one batch per frame and submitted without the CPU waiting for them, so the
/// only thing that orders a copy before the draw which samples it is queue submission order plus the barrier the
/// upload ends with. A mistake there does not throw and does not usually show: the copy nearly always wins the race
/// anyway, and the frame looks right — until a driver, a GPU, or a bigger asset changes the timing.
/// <para>
/// So this asserts the ordering directly, at the moment it is most likely to break: a mesh and a texture created
/// <em>between</em> two frames must be complete when the very next frame draws with them. That is the streaming
/// case, and the one the old drain-per-resource path made trivially safe by stalling.
/// </para>
/// GPU-gated — SKIPPED when no backend is available.
/// </summary>
[NotInParallel] // one GPU engine at a time
public class UploadOrderingTests
{
	private const int W = 64, H = 64;

	[Test]
	public void A_resource_created_between_frames_is_complete_when_the_next_frame_samples_it()
	{
		_ = new VulkanEngineSettings();
		_ = new DirectX12EngineSettings();
		if (Backends.Available.Count == 0)
			throw new TUnit.Core.Exceptions.SkipTestException("No GPU backend is available on this box (Backends.Available is empty).");

		foreach (var backend in Backends.Available)
		{
			// Two runs with different colours, because one run asserting "red" could be satisfied by uninitialised
			// memory that happened to be red. The same code path producing two different right answers cannot.
			var red = RenderLateResource(backend, 220, 20, 20);
			var blue = RenderLateResource(backend, 20, 20, 220);

			red.R.ShouldBeGreaterThan(140, $"{backend.Name}: the late-created texture's red did not reach the draw (got {red}).");
			red.B.ShouldBeLessThan(110, $"{backend.Name}: the late-created red texture sampled as blue (got {red}).");
			blue.B.ShouldBeGreaterThan(140, $"{backend.Name}: the late-created texture's blue did not reach the draw (got {blue}).");
			blue.R.ShouldBeLessThan(110, $"{backend.Name}: the late-created blue texture sampled as red (got {blue}).");
		}
	}

	// Render frames against a graph whose draw reads a mutable field, create the mesh + texture only after some
	// frames have already gone through, then capture the first frame that draws them. The closure reads the live
	// field rather than capturing a value, which is what lets the resources arrive mid-loop.
	private static Rgb RenderLateResource(IRenderBackend backend, byte r, byte g, byte b)
	{
		using var engine = backend.Create(Surface.Offscreen(W, H), W, H, new EngineSettings());
		engine.DefineTechnique("gbuffer.tex", new TechniqueDescriptor("gbuffer_textured", HasVertexInput: true));
		engine.DefineTechnique("blit", new TechniqueDescriptor("blit"));

		var late = new LateResources();
		ConstantsHandle viewproj = null!;
		var graph = RenderGraph.Define(gr =>
		{
			var screen = gr.Backbuffer;
			var albedo = gr.Screen.Color("albedo", Format.Rgba8Srgb);
			var normal = gr.Screen.Color("normal", Format.Rgba16F);
			var position = gr.Screen.Color("position", Format.Rgba16F);
			var depth = gr.Screen.Depth("depth", Format.Depth32F);
			viewproj = gr.Constants<Matrix4x4>("upload.viewproj");

			gr.Pass("gbuffer")
				.Reads(viewproj)
				.Writes(albedo, normal, position)
				.WritesDepth(depth)
				.Draw(ctx =>
				{
					// BuildQuad spans (0,0)..(1,1), so centre it on the origin the camera points at.
					if (late.Mesh is { } mesh && late.Material is { } material)
						DemoDraw.Mesh(ctx, mesh, "gbuffer.tex", Matrix4x4.CreateTranslation(-0.5f, -0.5f, 0f), 0, material);
				});

			gr.Pass("present").Samples(albedo).Writes(screen).Draw(ctx => ctx.FullScreen("blit", albedo));
			gr.Present(screen);
		});
		engine.SetGraph(graph);

		// Head-on, filling the frame, so the centre texel is unambiguously the quad's.
		var cam = new BinaryVibrance.Kaldra.Concepts.Camera(engine)
			.Perspective(0.9f, (float)W / H, 0.1f, 100f)
			.LookAt(new Vector3(0, 0, 1.2f), Vector3.Zero);
		var inputs = new FrameInputs { Time = 0, DeltaSeconds = 1.0 / 60.0 };
		engine.Write(viewproj, cam.ViewProjection);

		// Frames with nothing to draw: the upload batch is opened, submitted and retired at least once before the
		// resources under test are created, so they land in a *later* batch rather than the engine's first.
		for (var frame = 0; frame < 3; frame++)
			engine.Render(inputs);

		// Created between frames — the streaming case. Nothing waits for these copies.
		var (vertices, indices) = Geometry.BuildQuad();
		late.Mesh = engine.CreateMesh(vertices, indices);
		late.Material = engine.CreateMaterial("gbuffer.tex",
			engine.CreateTexture(TextureData.FromRgba8(1, 1, [r, g, b, 255], TextureFormat.Rgba8Srgb, generateMips: false)));

		var path = Path.Combine(Path.GetTempPath(), $"kaldra-upload-{Guid.NewGuid():N}.png");
		try
		{
			// The very next frame draws them. Capture it, then flush so the readback lands.
			engine.RequestCapture(path);
			for (var frame = 0; frame < 3; frame++)
				engine.Render(inputs);

			var (width, _, rgba) = PngReader.Read(path);
			var centre = (((H / 2) * width) + (W / 2)) * 4;
			return new Rgb(rgba[centre], rgba[centre + 1], rgba[centre + 2]);
		}
		finally
		{
			if (File.Exists(path))
				File.Delete(path);
		}
	}

	// A stable object the pass closure reads live — the "capture stable objects, not per-frame values" half of the
	// pure-closure rule, and the only way a resource can appear mid-loop in a graph built once.
	private sealed class LateResources
	{
		public Mesh? Mesh;
		public Material? Material;
	}

	private readonly record struct Rgb(int R, int G, int B);
}
