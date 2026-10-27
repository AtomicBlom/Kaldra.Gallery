using System.Numerics;
using System.Runtime.InteropServices;
using Loom.Backend.DirectX12;
using Loom.Backend.Vulkan;
using Loom.Core.Diagnostics;
using Loom.Core.Graph;
using Loom.Core.Hosting;
using Loom.Core.Materials;
using Loom.Gallery.Demos;
using Loom.UI.SUIsei;
using SUIsei.Drawing;
using LoomDrawCommand = Loom.Core.Hosting.DrawCommand;
using LoomOverlayVertex = Loom.Core.Geometry.OverlayVertex;
using SoftwareRasterizer = SUIsei.Rasterizer.SoftwareRasterizer;

namespace Loom.Tests;

/// <summary>
/// The GPU draws SUIsei's vector prim as SUIsei's software rasterizer does. The rasterizer is the CPU oracle for every
/// UI prim, and <c>ui.slang</c>'s vector arm is written to its maths: four interpolated half-plane distances, each
/// divided by the length of its screen-space gradient. So this draws <see cref="VectorPrimsDemo.BuildList"/> —
/// tessellated strokes in every join and cap, a dashed curve, a concave fill and a curved one, rotated shapes,
/// hairlines, and a translucent stroke in a group layer — through the <c>ui</c> technique on each desktop backend, onto
/// the demo's opaque background with nothing else in the frame, rasterizes the same list on the CPU, and diffs the two
/// at the cross-backend parity bar. The driver's debug layer must stay silent while it draws. The browser's half is
/// the VectorPrims leg of <see cref="WebGpuParityTests"/>, which holds WebGPU to Vulkan on the same list. GPU-gated.
/// </summary>
[NotInParallel] // one GPU engine at a time, and LoomLog.Sink is process-wide
public class VectorPrimRenderTests
{
	// The parity bar GalleryParityTests uses: a pixel fails if any channel differs by more than this.
	private const int PerChannelTolerance = 4;
	private const double MaxFailedFraction = 0.005;

	// Both renders start from the demo's opaque background. The rasterizer resolves to straight alpha while the GPU
	// target holds premultiplied colour, so only an opaque frame reads the same from both.
	private static readonly Vector4 Background = VectorPrimsDemo.Background;

	[Test]
	public void The_GPU_draws_the_vector_prims_as_SUIsei_s_rasterizer_does()
	{
		_ = new VulkanEngineSettings();
		_ = new DirectX12EngineSettings();
		if (Backends.Available.Count == 0)
			throw new TUnit.Core.Exceptions.SkipTestException("no GPU backend available on this box.");

		var width = (int)VectorPrimsDemo.Canvas.X;
		var height = (int)VectorPrimsDemo.Canvas.Y;
		var list = VectorPrimsDemo.BuildList();
		var oracle = new SoftwareRasterizer(width, height);
		oracle.Clear(Background);
		oracle.Render(list);
		var expected = oracle.ResolveToRgba8();

		// Vacuous-pass guard: a list that drew nothing would match a GPU that drew nothing.
		ImageComparison.DistinctColors(expected).ShouldBeGreaterThan(256, "the oracle's render of the vector list is near-empty");

		var failures = new List<string>();
		foreach (var backend in Backends.Available)
		{
			var path = Path.Combine(Path.GetTempPath(), $"loom-vectorprims-{backend.Name}-{Guid.NewGuid():N}.png");
			var errors = new List<string>();
			var previousSink = LoomLog.Sink;
			LoomLog.Sink = (level, message) =>
			{
				if (level == LoomLogLevel.Error)
					lock (errors) errors.Add(message);
			};
			try
			{
				RenderList(backend, list, width, height, path);
				LoomLog.Sink = previousSink;
				foreach (var error in errors)
					failures.Add($"{backend.Name}: the debug layer reported: {error}");

				var (w, h, actual) = PngReader.Read(path);
				if (w != width || h != height)
				{
					failures.Add($"{backend.Name}: captured {w}x{h}, expected {width}x{height}");
					continue;
				}
				var diff = ImageComparison.Compare(w, h, actual, expected, PerChannelTolerance);
				if (diff.FailedFraction > MaxFailedFraction)
					failures.Add($"{backend.Name} vs the rasterizer: {diff}");
			}
			finally
			{
				LoomLog.Sink = previousSink;
				if (File.Exists(path)) File.Delete(path);
			}
		}

		failures.ShouldBeEmpty("The GPU's vector prims differ from SUIsei's rasterizer:\n  " + string.Join("\n  ", failures));
	}

	// Draw `list` alone through the ui technique onto an offscreen target cleared to Background, and capture it: its slot table uploaded,
	// its vertices and commands reinterpreted as the engine's byte-identical structs, pushed as UIPainter pushes an SDR
	// list.
	private static void RenderList(IRenderBackend backend, DrawListData list, int width, int height, string outPath)
	{
		using var engine = backend.Create(Surface.Offscreen(width, height), width, height, new EngineSettings());
		UIPainter.Create(engine, new global::SUIsei.UIHost()); // defines the ui technique; this draws no host

		var slots = list.TextureSlots;
		var textures = new TextureRef[slots.Count];
		for (var i = 0; i < slots.Count; i++)
			textures[i] = engine.CreateTexture(TextureData.FromRgba8(slots[i].Width, slots[i].Height, slots[i].Pixels.Span, TextureFormat.Rgba8Unorm, generateMips: false));
		var vertices = MemoryMarshal.Cast<SUIsei.Drawing.OverlayVertex, LoomOverlayVertex>(list.Vertices.Span).ToArray();
		var indices = list.Indices.ToArray();
		var commands = MemoryMarshal.Cast<SUIsei.Drawing.DrawCommand, LoomDrawCommand>(list.Commands.Span).ToArray();
		var size = list.CoordinateSize;

		engine.SetGraph(RenderGraph.Define(g =>
		{
			g.BackbufferClear = Background;
			var screen = g.Backbuffer;
			g.Pass("vector").Writes(screen).Draw(ctx =>
			{
				Span<float> push = [2f / size.X, 2f / size.Y, -1f, -1f, 1f, 0f];
				ctx.DrawList("ui", DrawListIdentity.Transient, textures, vertices, indices, commands, push);
			});
			g.Present(screen);
		}));

		var inputs = new FrameInputs { Time = 0, DeltaSeconds = 1.0 / 60.0 };
		for (var frame = 1; frame <= 3; frame++)
		{
			if (frame == 2)
				engine.RequestCapture(outPath);
			engine.Render(inputs);
		}
	}
}
