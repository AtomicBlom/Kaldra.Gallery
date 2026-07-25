using System.Numerics;

using Loom.Backend.DirectX12;
using Loom.Backend.Vulkan;
using Loom.Core.Graph;
using Loom.Core.Hosting;
using Loom.UI.SUIsei;

using SUIsei;
using SUIsei.Controls;

namespace Loom.Tests;

/// <summary>
/// Controlled render tests that assert a <em>property/invariant</em> of the output (rather than diffing backends
/// against each other, as <see cref="TargetedParityTests"/> does, or against a baseline). Same discipline: a
/// minimal single-feature scene, one assertion. GPU-gated.
/// </summary>
[NotInParallel] // one GPU engine at a time
public class RenderPropertyTests
{
	/// <summary>
	/// Group opacity composites a faded subtree once — so within same-coloured opaque children, the region where
	/// two of them overlap is the <em>same</em> colour as where only one is. (A per-child fade would double-fade the
	/// overlap and darken it.) This is the one UI path whose pixels depend on the backend — the backend renders the
	/// faded subtree into a pooled offscreen layer and composites it back at the group alpha — so it is exactly the
	/// kind of feature worth a controlled invariant test. Rendered on each available backend independently.
	/// </summary>
	[Test]
	public void Group_opacity_keeps_the_overlap_the_same_colour_as_the_non_overlap()
	{
		_ = new VulkanEngineSettings();
		_ = new DirectX12EngineSettings();
		if (Engine.AvailableEngines.Count == 0)
			throw new TUnit.Core.Exceptions.SkipTestException("no GPU backend available on this box.");

		// Panel-pixel sample points, away from edges/corners: one inside only the first fill, one inside the overlap.
		var nonOverlap = new Vector2(110, 100);
		var overlap = new Vector2(185, 155);

		var failures = new List<string>();
		foreach (var backend in Engine.AvailableEngines)
		{
			var path = Path.Combine(Path.GetTempPath(), $"loom-groupopacity-{backend.Name}-{Guid.NewGuid():N}.png");
			try
			{
				RenderGroupOpacityPanel(backend, path);
				var (w, h, rgba) = PngReader.Read(path);

				var a = AverageBlock(rgba, w, h, nonOverlap, 6);
				var o = AverageBlock(rgba, w, h, overlap, 6);
				// The two regions must match closely — a per-child fade would leave the overlap visibly darker.
				var delta = MaxChannelDelta(a, o);
				if (delta > 4)
					failures.Add($"{backend.Name}: overlap {Fmt(o)} != non-overlap {Fmt(a)} (Δ{delta}) — the group fade darkened the overlap.");
				// Guard against a vacuous pass: both samples must land on the composited panel (non-zero alpha), not
				// the transparent background (rgba 0,0,0,0), where "overlap == non-overlap" would hold trivially.
				if (a.W < 8 || o.W < 8)
					failures.Add($"{backend.Name}: a sample missed the panel (non-overlap {Fmt(a)}, overlap {Fmt(o)}).");
			}
			finally
			{
				if (File.Exists(path)) File.Delete(path);
			}
		}

		failures.ShouldBeEmpty("Group-opacity composite is wrong:\n  " + string.Join("\n  ", failures));
	}

	private const int W = 512, H = 384;

	private static void RenderGroupOpacityPanel(IRenderBackend backend, string outPath)
	{
		using var engine = backend.Create(Surface.Offscreen(W, H), W, H, new EngineSettings());

		// InWorldPanel renders a SUIsei tree to a target through the standard "ui" draw-list path — including the
		// offscreen-layer composite a group-opacity subtree triggers. A flat identity transform, no billboard.
		var panel = InWorldPanel.Create(engine, W, H, Matrix4x4.Identity, billboard: false, fontAtlas: FontAtlas.Default);
		panel.View.Root.Content = BuildGroupOpacityPanel();

		var graph = RenderGraph.Define(g =>
		{
			var screen = g.Backbuffer;
			g.Pass("ui").Writes(screen).Draw(ctx => panel.Draw(ctx));
			g.Present(screen);
		});
		engine.SetGraph(graph);

		var inputs = new FrameInputs { Time = 0, DeltaSeconds = 1.0 / 60.0 };
		for (var frame = 1; frame <= 3; frame++)
		{
			panel.Tick(1.0 / 60.0, 0f, Matrix4x4.Identity, Matrix4x4.Identity, new Vector2(W, H));
			panel.Render();
			if (frame == 2)
				engine.RequestCapture(outPath);
			engine.Render(inputs);
		}
	}

	// A 50%-opacity container over a Canvas of two overlapping opaque fills of the SAME colour — the exact tree the
	// sandbox/web hosts build for the UI Group Opacity demo.
	private static UIElement BuildGroupOpacityPanel()
	{
		var overlapCanvas = new Canvas { Width = 240f, Height = 180f };
		var fillA = new Border { Width = 150f, Height = 120f, CornerRadius = 8f, Background = HdrColor.FromRgb(80, 170, 255) };
		var fillB = new Border { Width = 150f, Height = 120f, CornerRadius = 8f, Background = HdrColor.FromRgb(80, 170, 255) };
		Canvas.SetLeft(fillA, 0f);
		Canvas.SetTop(fillA, 0f);
		Canvas.SetLeft(fillB, 80f);
		Canvas.SetTop(fillB, 50f);
		overlapCanvas.Children.Add(fillA);
		overlapCanvas.Children.Add(fillB);
		return new Border
		{
			Margin = new Thickness(48f, 48f, 0f, 0f),
			HorizontalAlignment = HorizontalAlignment.Left,
			VerticalAlignment = VerticalAlignment.Top,
			Padding = new Thickness(24f),
			Background = HdrColor.FromRgb(18, 22, 30),
			CornerRadius = 14f,
			Opacity = 0.5f,
			Child = overlapCanvas,
		};
	}

	private readonly record struct Rgb(int R, int G, int B, int W);

	private static Rgb AverageBlock(byte[] rgba, int width, int height, Vector2 center, int half)
	{
		long r = 0, g = 0, b = 0, a = 0;
		var n = 0;
		for (var y = (int)center.Y - half; y <= (int)center.Y + half; y++)
			for (var x = (int)center.X - half; x <= (int)center.X + half; x++)
			{
				if ((uint)x >= (uint)width || (uint)y >= (uint)height)
					continue;
				var i = (y * width + x) * 4;
				r += rgba[i]; g += rgba[i + 1]; b += rgba[i + 2]; a += rgba[i + 3];
				n++;
			}
		return n == 0 ? default : new Rgb((int)(r / n), (int)(g / n), (int)(b / n), (int)(a / n));
	}

	private static int MaxChannelDelta(Rgb a, Rgb b) =>
		Math.Max(Math.Max(Math.Abs(a.R - b.R), Math.Abs(a.G - b.G)), Math.Abs(a.B - b.B));

	private static string Fmt(Rgb c) => $"rgba({c.R},{c.G},{c.B},{c.W})";
}
