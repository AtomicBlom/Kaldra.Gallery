using System.Numerics;
using System.Runtime.InteropServices;
using Loom.Backend.DirectX12;
using Loom.Backend.Vulkan;
using Loom.Core.Diagnostics;
using Loom.Core.Graph;
using Loom.Core.Hosting;
using Loom.Core.Materials;
using Loom.Gallery.Demos;
using Loom.Gallery.UI;
using Loom.UI.SUIsei;
using SUIsei.Drawing;
using SUIsei.Extensions;
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
/// the VectorPrims leg of <see cref="WebGpuParityTests"/>, which holds WebGPU to Vulkan on the same list.
/// <para>
/// The same comparison runs over a list SUIsei's compositor made rather than one built by hand: the page of
/// <see cref="VectorUIDemo"/>, composed from its XAML — shapes and their strokes, dashes, a trimmed stroke whose gradient
/// runs along its length, gradient fills, a translucent stroke in a group layer, a clip, an image on rounded corners
/// and glyph runs — so the GPU is held to the oracle on what a compositor emits for shapes, not only on hand-built
/// triangles. Both lists are held to the same bar.
/// </para>
/// <para>
/// What the bar absorbs, measured on an RTX 2080 Ti with both desktop backends identical: the vector-prims list differs
/// from the oracle by at most 2/255 anywhere. The page differs by at most 3/255 except in one row of 130 pixels, the
/// bottom edge of its clip card's <c>UIElement.Clip</c>, which layout places between two pixel rows. There a hard clip's
/// two implementations disagree by one row: <see cref="Loom.Core.Hosting.DrawListGeometry.Scissor"/> truncates a
/// fractional far edge, so a row less than half inside the clip is cut, while the rasterizer rounds the far edge out and
/// draws it. GPU-gated.
/// </para>
/// </summary>
[NotInParallel] // one GPU engine at a time, and LoomLog.Sink is process-wide
public class VectorPrimRenderTests
{
	// The parity bar GalleryParityTests uses: a pixel fails if any channel differs by more than this.
	private const int PerChannelTolerance = 4;
	private const double MaxFailedFraction = 0.005;

	[Test]
	public void The_GPU_draws_the_vector_prims_as_SUIsei_s_rasterizer_does()
	{
		var width = (int)VectorPrimsDemo.Canvas.X;
		var height = (int)VectorPrimsDemo.Canvas.Y;
		AssertGpuMatchesRasterizer("vectorprims", VectorPrimsDemo.BuildList(), width, height, VectorPrimsDemo.Background);
	}

	[Test]
	public void The_GPU_draws_the_vector_UI_page_as_SUIsei_s_rasterizer_does()
	{
		var size = new System.Numerics.Vector2(GalleryCapture.Width, GalleryCapture.Height);
		var view = new VectorUIView();
		// Part-way through its draw-on, so the list holds a trim with both ends inside the stroke.
		StrokeTrim.SetStart(view.Trail, 0.15f);
		StrokeTrim.SetEnd(view.Trail, 0.8f);
		var host = new global::SUIsei.UIHost();
		var root = host.CreateRoot(global::SUIsei.UILayer.Default).Root;
		root.Content = view;
		host.Tick(0, 0f, size);
		AssertGpuMatchesRasterizer("vectorui", root.Render(), GalleryCapture.Width, GalleryCapture.Height, VectorUIDemo.Background);
	}

	// Rasterize `list` on the CPU, draw it on each desktop backend, and hold every backend to the oracle at the parity bar,
	// with the debug layer silent. Both renders start from the opaque `background`: the rasterizer resolves to straight
	// alpha while the GPU target holds premultiplied colour, so only an opaque frame reads the same from both.
	private static void AssertGpuMatchesRasterizer(string name, DrawListData list, int width, int height, Vector4 background)
	{
		_ = new VulkanEngineSettings();
		_ = new DirectX12EngineSettings();
		if (Backends.Available.Count == 0)
			throw new TUnit.Core.Exceptions.SkipTestException("no GPU backend available on this box.");

		var oracle = new SoftwareRasterizer(width, height);
		oracle.Clear(background);
		oracle.Render(list);
		var expected = oracle.ResolveToRgba8();

		// Vacuous-pass guard: a list that drew nothing would match a GPU that drew nothing.
		ImageComparison.DistinctColors(expected).ShouldBeGreaterThan(256, $"the oracle's render of the {name} list is near-empty");

		var failures = new List<string>();
		foreach (var backend in Backends.Available)
		{
			var path = Path.Combine(Path.GetTempPath(), $"loom-{name}-{backend.Name}-{Guid.NewGuid():N}.png");
			var errors = new List<string>();
			var previousSink = LoomLog.Sink;
			LoomLog.Sink = (level, message) =>
			{
				if (level == LoomLogLevel.Error)
					lock (errors) errors.Add(message);
			};
			try
			{
				RenderList(backend, list, width, height, background, path);
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

		failures.ShouldBeEmpty($"The GPU's {name} list differs from SUIsei's rasterizer:\n  " + string.Join("\n  ", failures));
	}

	// Draw `list` alone through the ui technique onto an offscreen target cleared to `background`, and capture it: its slot
	// table uploaded and its commands tagged by UIPainter's own helpers, its vertices reinterpreted as the engine's
	// byte-identical struct, pushed as UIPainter pushes an SDR list.
	private static void RenderList(IRenderBackend backend, DrawListData list, int width, int height, Vector4 background, string outPath)
	{
		using var engine = backend.Create(Surface.Offscreen(width, height), width, height, new EngineSettings());
		UIPainter.Create(engine, new global::SUIsei.UIHost()); // defines the ui technique; this draws no host

		var slots = list.TextureSlots;
		var textures = new TextureRef[slots.Count];
		for (var i = 0; i < slots.Count; i++)
			textures[i] = engine.CreateTexture(UIPainter.UploadData(slots[i]));
		var vertices = MemoryMarshal.Cast<SUIsei.Drawing.OverlayVertex, LoomOverlayVertex>(list.Vertices.Span).ToArray();
		var indices = list.Indices.ToArray();
		var commands = new LoomDrawCommand[list.Commands.Length];
		UIPainter.CopyCommands(list.Commands.Span, slots, commands);
		var size = list.CoordinateSize;

		engine.SetGraph(RenderGraph.Define(g =>
		{
			g.BackbufferClear = background;
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
