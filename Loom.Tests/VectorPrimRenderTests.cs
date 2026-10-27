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
using SUIsei;
using SUIsei.Controls;
using SUIsei.Drawing;
using SUIsei.Extensions;
using SUIsei.Media;
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
/// runs along its length, gradient fills, a translucent stroke in a group layer, a clip, an image on rounded corners,
/// glyph runs and their outlines and shadows — so the GPU is held to the oracle on what a compositor emits for shapes,
/// not only on hand-built triangles. A third list is text alone with every effect <c>suisei:TextEffects</c> draws (solid
/// and gradient outlines, soft and hard shadows, an outline over a glow, a translucent outline on a faded run, and small
/// text whose outline the atlas's field clamps), at layout scales 1 and 1.5, so the glyph arm's true-distance branch and
/// the run's screenPxRange under scale are held to the oracle too. All three lists are held to the same bar.
/// </para>
/// <para>
/// What the bar absorbs, measured on an RTX 2080 Ti with both desktop backends identical: the vector-prims list differs
/// from the oracle by at most 2/255 anywhere. The text-effects lists differ by at most 3/255 except in at most 39 pixels
/// (0.01%), none by more than 9/255. The page differs by at most 3/255 except in about 130 pixels, nearly all one row:
/// the bottom edge of its clip card's <c>UIElement.Clip</c>, which layout places between two pixel rows. There a hard
/// clip's two implementations disagree by one row: <see cref="Loom.Core.Hosting.DrawListGeometry.Scissor"/> truncates a
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

	[Test]
	[Arguments(1f)]
	[Arguments(1.5f)]
	public void The_GPU_draws_text_effects_as_SUIsei_s_rasterizer_does(float layoutScale)
	{
		const int Width = 960, Height = 540;
		var host = new UIHost();
		var root = host.CreateRoot(new UILayer(0, layoutScale)).Root;
		root.Content = BuildTextEffects();
		host.Tick(0, 0f, new Vector2(Width, Height));
		AssertGpuMatchesRasterizer($"texteffects-{layoutScale}x", root.Render(), Width, Height, TextEffectsBackground);
	}

	// A mid-dark ground, so black shadows and outlines show against it as well as light ones.
	private static readonly Vector4 TextEffectsBackground = new(0.05f, 0.06f, 0.08f, 1f);

	// Every effect at a few sizes: solid and gradient outlines, soft and hard shadows, an outline over a shadow, a
	// translucent effect, a run faded as a whole (one group layer), and small text whose outline the field clamps.
	private static UIElement BuildTextEffects()
	{
		var panel = new StackPanel { Margin = new Thickness(16f) };
		void Add(string text, float size, Action<TextBlock> effects, float opacity = 1f)
		{
			var block = new TextBlock { Text = text, FontSize = size, Foreground = Solid(236, 239, 246), Opacity = opacity, Margin = new Thickness(0f, 0f, 0f, 6f) };
			effects(block);
			panel.Children.Add(block);
		}

		Add("Outline 8SsB$ 1:23.456", 36f, t =>
		{
			TextEffects.SetOutlineThickness(t, 0.1f);
			TextEffects.SetOutlineBrush(t, Solid(10, 12, 20));
		});
		Add("Gradient outline LAP 3", 32f, t =>
		{
			TextEffects.SetOutlineThickness(t, 0.12f);
			var gradient = new LinearGradientBrush { StartPoint = new Point(0, 0), EndPoint = new Point(1, 0) };
			gradient.GradientStops.Add(new GradientStop { Color = HdrColor.FromRgb(255, 77, 109), Offset = 0f });
			gradient.GradientStops.Add(new GradientStop { Color = HdrColor.FromRgb(255, 176, 0), Offset = 1f });
			TextEffects.SetOutlineBrush(t, gradient);
		});
		Add("Soft shadow 0:59.404", 32f, t =>
		{
			TextEffects.SetShadowOffset(t, new Point(0.05f, 0.08f));
			TextEffects.SetShadowSoftness(t, 0.3f);
			TextEffects.SetShadowBrush(t, Solid(0, 0, 0, 200));
		});
		Add("Hard shadow GO!", 32f, t =>
		{
			TextEffects.SetShadowOffset(t, new Point(0.08f, 0.08f));
			TextEffects.SetShadowBrush(t, Solid(255, 59, 107));
		});
		Add("Outline over a glow P1", 28f, t =>
		{
			TextEffects.SetOutlineThickness(t, 0.06f);
			TextEffects.SetOutlineBrush(t, Solid(10, 12, 20));
			TextEffects.SetShadowSoftness(t, 0.35f);
			TextEffects.SetShadowBrush(t, Solid(64, 200, 255, 176));
		});
		Add("Translucent outline, faded run", 28f, t =>
		{
			TextEffects.SetOutlineThickness(t, 0.08f);
			TextEffects.SetOutlineBrush(t, Solid(58, 123, 213, 128));
			TextEffects.SetShadowOffset(t, new Point(0.06f, 0.06f));
			TextEffects.SetShadowBrush(t, Solid(0, 0, 0));
		}, opacity: 0.6f);
		Add("Small text, outlined and shadowed: 0123456789", 12f, t =>
		{
			TextEffects.SetOutlineThickness(t, 0.15f);
			TextEffects.SetOutlineBrush(t, Solid(10, 12, 20));
			TextEffects.SetShadowOffset(t, new Point(0.1f, 0.1f));
			TextEffects.SetShadowSoftness(t, 0.2f);
			TextEffects.SetShadowBrush(t, Solid(255, 176, 0, 160));
		});
		return panel;

		static SolidColorBrush Solid(byte r, byte g, byte b, byte a = 255) => new(HdrColor.FromRgb(r, g, b, a));
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
