using System.Numerics;

using Loom.Backend.DirectX12;
using Loom.Backend.Vulkan;
using Loom.Concepts;
using Loom.Core.Diagnostics;
using Loom.Core.Geometry;
using Loom.Core.Graph;
using Loom.Core.Hosting;
using Loom.Core.Materials;
using Loom.Core.Primitives;
using Loom.Gallery.Demos;
using Loom.UI.SUIsei;

using SUIsei;
using SUIsei.Controls;
using SUIsei.Media;

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
	/// Replacing a live engine's graph does not change what the next frame looks like — the property a running app
	/// relies on every time it switches demos. Nothing else in the suite swaps a graph on an engine that has already
	/// rendered; every other GPU test brings up a fresh engine per capture.
	/// <para>
	/// It exists alongside cache invalidation: a swap destroys every transient of the outgoing graph, so anything a
	/// backend cached against those GPU objects must be dropped with them — Vulkan caches descriptor sets by the
	/// image views they name. <b>Be clear about what this does and does not prove.</b> Deleting the Vulkan flush and
	/// re-running this test was tried, and it still passed: the stale entries are only reachable if the driver hands
	/// back a recycled <c>VkImageView</c> handle that collides with one, and this driver did not. Handle reuse is
	/// legal and common, so the flush is required regardless — but this test is coverage of the swap path, not a
	/// proof of the flush, and a future change that drops the invalidation will not necessarily be caught here.
	/// </para>
	/// <para>
	/// The bar is 1/255, not bit-identity, and the reason is measured rather than assumed: the warm-up frames that
	/// populate the cache also advance delta-driven state (auto-exposure, particles), which leaves the post-swap
	/// frame fractionally more converged than a cold one. Both backends show it, at the same magnitude, and
	/// shortening the warm-up from 62 frames to 3 halved the affected pixels (0.48% → 0.15% on Vulkan, 0.41% →
	/// 0.11% on DirectX 12) while <c>maxDiff</c> stayed at 1 — the signature of convergence, not of a bad binding.
	/// It costs this test nothing: a set left pointing at a destroyed image does not render one LSB dark, it
	/// renders garbage.
	/// </para>
	/// </summary>
	[Test]
	public void Swapping_the_graph_does_not_change_the_next_frame()
	{
		_ = new VulkanEngineSettings();
		_ = new DirectX12EngineSettings();
		if (Backends.Available.Count == 0)
			throw new TUnit.Core.Exceptions.SkipTestException("no GPU backend available on this box.");

		const int PerChannelTolerance = 1;
		var compared = 0;
		var failures = new List<string>();
		foreach (var backend in Backends.Available)
		{
			var fresh = Path.Combine(Path.GetTempPath(), $"loom-swap-{backend.Name}-fresh-{Guid.NewGuid():N}.png");
			var swapped = Path.Combine(Path.GetTempPath(), $"loom-swap-{backend.Name}-after-{Guid.NewGuid():N}.png");
			try
			{
				// The busiest scene on purpose: deferred lighting, shadows, SSAO, particles, bloom and tone-map give
				// the most transients to reallocate and the most cached bindings to invalidate.
				if (!GalleryCapture.TryRender(backend, () => new FullReferenceDemo(), fresh) ||
					!GalleryCapture.TryRenderAfterGraphSwap(backend, () => new FullReferenceDemo(), swapped))
					continue;

				var (w, h, a) = PngReader.Read(fresh);
				var (_, _, b) = PngReader.Read(swapped);
				compared++;

				// Guard against a vacuous pass: a swap that broke rendering outright could leave two frames that are
				// both blank, and blank compares equal to blank.
				var colors = ImageComparison.DistinctColors(b);
				if (colors < 64)
				{
					failures.Add($"{backend.Name}: the post-swap frame has only {colors} distinct colours — it did not render.");
					continue;
				}

				var diff = ImageComparison.Compare(w, h, a, b, PerChannelTolerance);
				if (diff.FailedFraction > 0)
					failures.Add($"{backend.Name}: the frame after a graph swap differs from a first frame: {diff}");
			}
			finally
			{
				if (File.Exists(fresh)) File.Delete(fresh);
				if (File.Exists(swapped)) File.Delete(swapped);
			}
		}

		compared.ShouldBeGreaterThan(0, "no backend rendered the demo, so no graph swap was exercised.");
		failures.ShouldBeEmpty("A graph swap changed the rendered frame:\n  " + string.Join("\n  ", failures));
	}

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
		if (Backends.Available.Count == 0)
			throw new TUnit.Core.Exceptions.SkipTestException("no GPU backend available on this box.");

		// Panel-pixel sample points, away from edges/corners: one inside only the first fill, one inside the overlap.
		var nonOverlap = new Vector2(110, 100);
		var overlap = new Vector2(185, 155);

		var failures = new List<string>();
		foreach (var backend in Backends.Available)
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

	/// <summary>
	/// Nested group opacity multiplies: a 50% group inside a 50% group renders the same pixels as one 25% group. This
	/// is the depth-2 case of the layer state machine, and the only thing that exercises the <em>nested</em> resume
	/// path — where ending an inner layer resumes the parent <em>layer</em> rather than the pass's own attachments.
	/// The sibling test above only ever opens one layer, so a backend could have that branch wrong and still pass it.
	/// <para>
	/// Both properties are asserted: the overlap invariant still holds two levels down, and the two-step composite
	/// agrees with the one-step one. The second is the stronger claim — the nested path routes the content through an
	/// extra 8-bit layer target, so a bound is a bound on that quantisation as well as on the maths.
	/// </para>
	/// </summary>
	[Test]
	public void Nested_group_opacity_multiplies_to_the_same_pixels_as_one_group()
	{
		_ = new VulkanEngineSettings();
		_ = new DirectX12EngineSettings();
		if (Backends.Available.Count == 0)
			throw new TUnit.Core.Exceptions.SkipTestException("no GPU backend available on this box.");

		// Panel-pixel sample points for the layout BuildFadedFills produces: one inside the first fill only, one
		// inside the overlap, both well clear of the rounded corners.
		var nonOverlap = new Vector2(80, 70);
		var overlap = new Vector2(160, 130);

		var failures = new List<string>();
		foreach (var backend in Backends.Available)
		{
			var nestedPath = Path.Combine(Path.GetTempPath(), $"loom-nestedopacity-{backend.Name}-{Guid.NewGuid():N}.png");
			var flatPath = Path.Combine(Path.GetTempPath(), $"loom-flatopacity-{backend.Name}-{Guid.NewGuid():N}.png");
			try
			{
				RenderPanel(backend, nestedPath, () => BuildFadedFills(0.5f, 0.5f));
				RenderPanel(backend, flatPath, () => BuildFadedFills(0.25f));
				var (nw, nh, nested) = PngReader.Read(nestedPath);
				var (fw, fh, flat) = PngReader.Read(flatPath);
				(nw, nh).ShouldBe((fw, fh));

				var nestedNon = AverageBlock(nested, nw, nh, nonOverlap, 6);
				var nestedOver = AverageBlock(nested, nw, nh, overlap, 6);
				var flatNon = AverageBlock(flat, fw, fh, nonOverlap, 6);

				// Vacuous-pass guard first: two transparent frames agree on everything.
				if (nestedNon.W < 8 || nestedOver.W < 8 || flatNon.W < 8)
				{
					failures.Add($"{backend.Name}: a sample missed the panel (nested {Fmt(nestedNon)}/{Fmt(nestedOver)}, flat {Fmt(flatNon)}).");
					continue;
				}

				// The overlap invariant, two layers deep.
				var overlapDelta = MaxChannelDelta(nestedNon, nestedOver);
				if (overlapDelta > 4)
					failures.Add($"{backend.Name}: nested overlap {Fmt(nestedOver)} != non-overlap {Fmt(nestedNon)} (Δ{overlapDelta}) — a nested group double-faded its overlap.");

				// And 0.5 x 0.5 lands where 0.25 does. A wrong nested resume shows up here as the inner subtree
				// composited into the pass target instead of into its parent layer, which fades it once, not twice.
				var multiplyDelta = MaxChannelDelta(nestedNon, flatNon);
				if (multiplyDelta > 4)
					failures.Add($"{backend.Name}: nested 0.5x0.5 {Fmt(nestedNon)} != flat 0.25 {Fmt(flatNon)} (Δ{multiplyDelta}) — nested group alphas did not multiply.");
			}
			finally
			{
				if (File.Exists(nestedPath)) File.Delete(nestedPath);
				if (File.Exists(flatPath)) File.Delete(flatPath);
			}
		}

		failures.ShouldBeEmpty("Nested group-opacity composite is wrong:\n  " + string.Join("\n  ", failures));
	}

	/// <summary>
	/// A group nested past the layer cap must not disturb what is drawn <em>beside</em> it. Layers cost a pooled
	/// offscreen target each, so the depth is capped; the hazard is suppressing a <c>BeginLayer</c> without suppressing
	/// its matching <c>EndLayer</c>, which pops one layer too early and leaves every enclosing group compositing into
	/// the wrong parent.
	/// <para>
	/// The shape matters, and cost me a wrong prediction: with the excess group's markers <em>adjacent</em> to the ones
	/// around it, the early pop composites the right layer into the right parent one marker sooner and nothing is drawn
	/// in between, so the frame is identical and depth alone proves nothing. The damage needs a sibling drawn after the
	/// over-deep group — that sibling then lands in a target that has already been resolved. So the tree here puts one
	/// beside it, and the assertion is that the sibling renders exactly as it does with the over-deep group removed.
	/// </para>
	/// </summary>
	[Test]
	public void A_group_nested_past_the_layer_cap_does_not_disturb_its_siblings()
	{
		_ = new VulkanEngineSettings();
		_ = new DirectX12EngineSettings();
		if (Backends.Available.Count == 0)
			throw new TUnit.Core.Exceptions.SkipTestException("no GPU backend available on this box.");

		// The sibling sits where nothing else covers it, so its colour is attributable to the layer stack alone.
		var sibling = new Vector2(160, 130);

		var failures = new List<string>();
		foreach (var backend in Backends.Available)
		{
			var deepPath = Path.Combine(Path.GetTempPath(), $"loom-deepopacity-{backend.Name}-{Guid.NewGuid():N}.png");
			var refPath = Path.Combine(Path.GetTempPath(), $"loom-deepopacity-ref-{backend.Name}-{Guid.NewGuid():N}.png");
			try
			{
				// Both trees nest exactly to the cap and hold the same sibling; only one also holds an over-deep group.
				RenderPanel(backend, deepPath, () => BuildCappedNestingWithSibling(overDeepGroup: true));
				RenderPanel(backend, refPath, () => BuildCappedNestingWithSibling(overDeepGroup: false));
				var (dw, dh, deep) = PngReader.Read(deepPath);
				var (rw, rh, reference) = PngReader.Read(refPath);
				(dw, dh).ShouldBe((rw, rh));

				var deepSibling = AverageBlock(deep, dw, dh, sibling, 6);
				var refSibling = AverageBlock(reference, rw, rh, sibling, 6);

				// Vacuous-pass guard: the reference sibling has to actually be on screen for the diff to mean anything.
				if (refSibling.W < 32)
				{
					failures.Add($"{backend.Name}: the reference sibling did not render ({Fmt(refSibling)}) — the comparison would be vacuous.");
					continue;
				}

				var delta = MaxChannelDelta(deepSibling, refSibling);
				if (delta > 4)
					failures.Add($"{backend.Name}: sibling {Fmt(deepSibling)} != {Fmt(refSibling)} without the over-deep group (Δ{delta}) — the excess layer desynchronised the stack.");
			}
			finally
			{
				if (File.Exists(deepPath)) File.Delete(deepPath);
				if (File.Exists(refPath)) File.Delete(refPath);
			}
		}

		failures.ShouldBeEmpty("A group nested past the layer cap disturbed its siblings:\n  " + string.Join("\n  ", failures));
	}

	/// <summary>
	/// Tone-mapping maps an HDR scene into the display range with a <em>monotonic</em> curve — brighter in →
	/// brighter (never dimmer) out — and actually spreads the HDR range across the display range rather than
	/// crushing it to one level. Feed a controlled 0→HDR gradient through the real exposure + tone-map stages and
	/// assert those hold across the row (a correctness/invariant check, not a curve reproduction). Absolute levels
	/// shift with the temporal auto-exposure in the loop, so this asserts shape, not exact values. On each backend.
	/// </summary>
	[Test]
	public void Tonemap_is_monotonic_across_the_hdr_range()
	{
		_ = new VulkanEngineSettings();
		_ = new DirectX12EngineSettings();
		if (Backends.Available.Count == 0)
			throw new TUnit.Core.Exceptions.SkipTestException("no GPU backend available on this box.");

		var failures = new List<string>();
		foreach (var backend in Backends.Available)
		{
			var path = Path.Combine(Path.GetTempPath(), $"loom-tonemap-{backend.Name}-{Guid.NewGuid():N}.png");
			try
			{
				RenderTonemappedGradient(backend, path);
				var (w, h, rgba) = PngReader.Read(path);

				// Sample luminance along the middle row at increasing input (uv.x → 0..1 → HDR 0..bright).
				var xs = new[] { 0.03f, 0.25f, 0.5f, 0.75f, 0.97f };
				var lum = xs.Select(fx => Luma(AverageBlock(rgba, w, h, new Vector2(fx * (w - 1), h / 2f), 4))).ToArray();

				// Monotonic non-decreasing (a few units of slack for sampling/quantisation).
				for (var i = 1; i < lum.Length; i++)
					if (lum[i] < lum[i - 1] - 3)
						failures.Add($"{backend.Name}: tone-map not monotonic at step {i}: {string.Join(" -> ", lum)}");
				// The HDR range is actually used — the bright end maps well above the dark end, not crushed flat.
				if (lum[^1] - lum[0] < 80)
					failures.Add($"{backend.Name}: the HDR range was not spread across the display range ({lum[0]} -> {lum[^1]}).");
			}
			finally
			{
				if (File.Exists(path)) File.Delete(path);
			}
		}

		failures.ShouldBeEmpty("Tone-map property violations:\n  " + string.Join("\n  ", failures));
	}

	// Probe a 0→HDR gradient into a linear HDR target, meter its exposure (the real temporal compute loop), tone-map
	// it to the display range, and blit the result out. Frozen inputs + enough frames for exposure to settle.
	private static void RenderTonemappedGradient(IRenderBackend backend, string outPath)
	{
		using var engine = backend.Create(Surface.Offscreen(W, H), W, H, new EngineSettings());
		engine.DefineTechnique("probe.opaque", new TechniqueDescriptor("probe", HasFragmentPush: true));
		engine.DefineTechnique("exposure", TechniqueDescriptor.Compute("exposure", hasFragmentPush: true));
		engine.DefineTechnique("tonemap", new TechniqueDescriptor("tonemap", HasFragmentPush: true));
		engine.DefineTechnique("blit", new TechniqueDescriptor("blit"));

		float[] hdr = [8f, 8f, 8f, 1f]; // 0→8 across the screen — well into HDR, so the highlights must roll off
		var graph = RenderGraph.Define(g =>
		{
			var screen = g.Backbuffer;
			var lit = g.Screen.Color("lit", Format.Rgba16F);
			var meter = g.Domain("meter", 1, 1);
			var exposure = meter.Persistent("exposure", Format.R32F);
			var graded = g.Screen.Color("graded", Format.Rgba16F);

			g.Pass("probe").Writes(lit).Draw(ctx => ctx.FullScreen("probe.opaque", hdr));
			g.Compute("exposure").Samples(lit).ReadWrites(exposure)
				.Dispatch(ctx => ctx.Dispatch("exposure", (1, 1, 1), [8f * (float)ctx.DeltaTime], [lit], [exposure]));
			g.Pass("tonemap").Samples(lit, exposure).Writes(graded)
				.Draw(ctx => ctx.FullScreen("tonemap", [0f], lit, exposure)); // 0 = SDR: roll highlights off to [0,1]
			g.Pass("blit").Samples(graded).Writes(screen).Draw(ctx => ctx.FullScreen("blit", graded));
			g.Present(screen);
		});
		engine.SetGraph(graph);

		var inputs = new FrameInputs { Time = 1.0, DeltaSeconds = 1.0 / 60.0 };
		for (var frame = 1; frame <= 62; frame++) // let the temporal auto-exposure converge before capturing
		{
			if (frame == 60)
				engine.RequestCapture(outPath);
			engine.Render(inputs);
		}
	}

	private static int Luma(Rgb c) => (int)(0.299 * c.R + 0.587 * c.G + 0.114 * c.B);

	private const int W = 512, H = 384;

	private static void RenderGroupOpacityPanel(IRenderBackend backend, string outPath) =>
		RenderPanel(backend, outPath, BuildGroupOpacityPanel);

	private static void RenderPanel(IRenderBackend backend, string outPath, Func<UIElement> content)
	{
		using var engine = backend.Create(Surface.Offscreen(W, H), W, H, new EngineSettings());

		// InWorldPanel renders a SUIsei tree to a target through the standard "ui" draw-list path — including the
		// offscreen-layer composite a group-opacity subtree triggers. A flat identity transform, no billboard.
		var panel = InWorldPanel.Create(engine, W, H, Matrix4x4.Identity, billboard: false);
		panel.View.Root.Content = content();

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
		var fill = new SolidColorBrush(HdrColor.FromRgb(80, 170, 255));
		var fillA = new Border { Width = 150f, Height = 120f, CornerRadius = new CornerRadius(8f), Background = fill };
		var fillB = new Border { Width = 150f, Height = 120f, CornerRadius = new CornerRadius(8f), Background = fill };
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
			Background = new SolidColorBrush(HdrColor.FromRgb(18, 22, 30)),
			CornerRadius = new CornerRadius(14f),
			Opacity = 0.5f,
			Child = overlapCanvas,
		};
	}

	// The same two overlapping same-coloured opaque fills, wrapped in one group-opacity container per entry in
	// `opacities` (outermost first). The fills' geometry does not depend on the nesting — the wrappers shrink to their
	// child and only the outermost carries a margin — so a nested tree and a flat one are pixel-comparable, which is
	// what lets "0.5 inside 0.5" be diffed against "0.25".
	private static UIElement BuildFadedFills(params float[] opacities)
	{
		var canvas = new Canvas { Width = 240f, Height = 180f };
		var fill = new SolidColorBrush(HdrColor.FromRgb(80, 170, 255));
		var fillA = new Border { Width = 150f, Height = 120f, CornerRadius = new CornerRadius(8f), Background = fill };
		var fillB = new Border { Width = 150f, Height = 120f, CornerRadius = new CornerRadius(8f), Background = fill };
		Canvas.SetLeft(fillA, 0f);
		Canvas.SetTop(fillA, 0f);
		Canvas.SetLeft(fillB, 80f);
		Canvas.SetTop(fillB, 50f);
		canvas.Children.Add(fillA);
		canvas.Children.Add(fillB);

		return Nest(canvas, opacities);
	}

	// Nesting to exactly the layer cap, whose innermost content is a Canvas holding a fill drawn *after* an optional
	// over-deep group. The group is the one level past the cap; the fill after it is the sibling whose target the
	// group's mis-paired EndLayer would resolve early.
	private static UIElement BuildCappedNestingWithSibling(bool overDeepGroup)
	{
		var canvas = new Canvas { Width = 240f, Height = 180f };
		if (overDeepGroup)
		{
			var deep = new Border
			{
				Width = 100f,
				Height = 60f,
				Opacity = 0.9f,
				Child = new Border { Background = new SolidColorBrush(HdrColor.FromRgb(255, 120, 60)) },
			};
			Canvas.SetLeft(deep, 0f);
			Canvas.SetTop(deep, 0f);
			canvas.Children.Add(deep);
		}
		var siblingFill = new Border { Width = 120f, Height = 100f, Background = new SolidColorBrush(HdrColor.FromRgb(80, 170, 255)) };
		Canvas.SetLeft(siblingFill, 60f);
		Canvas.SetTop(siblingFill, 50f);
		canvas.Children.Add(siblingFill);

		return Nest(canvas, Enumerable.Repeat(0.9f, DrawListGeometry.MaxLayerNesting).ToArray());
	}

	// Wrap `content` in one group-opacity Border per entry (outermost first); only the outermost carries the margin, so
	// the content lands in the same place however deep the nesting is.
	private static UIElement Nest(UIElement content, float[] opacities)
	{
		for (var i = opacities.Length - 1; i >= 0; i--)
			content = new Border
			{
				Margin = i == 0 ? new Thickness(48f, 48f, 0f, 0f) : new Thickness(0f),
				HorizontalAlignment = HorizontalAlignment.Left,
				VerticalAlignment = VerticalAlignment.Top,
				Opacity = opacities[i],
				Child = content,
			};
		return content;
	}

	/// <summary>
	/// The <c>ui</c> shader antialiases an edge over one screen pixel whatever the edge's angle: it divides a distance by
	/// the length of its screen-space gradient. <c>fwidth</c> — the L1 norm |ddx| + |ddy| — would over-state that
	/// gradient by √2 on a diagonal and blur a 45° edge across √2 as many pixels as a vertical one, which is the
	/// difference this test exists to catch.
	/// <para>
	/// Both arms that take edge coverage from a distance are checked: a rounded rect, whose distance is its own SDF in
	/// local rect space, and a vector triangle, whose distance arrives interpolated in <c>Custom</c>, with its three other
	/// half-plane slots unused. Each draws one straight edge through the middle of the target, at 0° and at 45°. The edge
	/// is known exactly, so every pixel's signed distance <c>s</c> from it is too, and its coverage is the capture's
	/// alpha; the ramp width is the least-squares fit of <c>coverage − 0.5 = s / width</c> over the partly covered pixels.
	/// The driver's debug layer must stay silent throughout, so the two arms are held to validation as well.
	/// </para>
	/// </summary>
	[Test]
	public void An_edge_ramps_across_one_pixel_at_0_and_45_degrees()
	{
		_ = new VulkanEngineSettings();
		_ = new DirectX12EngineSettings();
		if (Backends.Available.Count == 0)
			throw new TUnit.Core.Exceptions.SkipTestException("no GPU backend available on this box.");

		// Off the pixel grid on both axes, so the pixels the edge crosses are partly covered rather than exactly half.
		var edge = new Vector2(256.2f, 192.45f);
		(string Name, uint Kind)[] arms = [("rounded rect", 1), ("vector", 7)];

		var failures = new List<string>();
		foreach (var backend in Backends.Available)
		{
			foreach (var (name, kind) in arms)
			{
				var widths = new double[2];
				for (var a = 0; a < 2; a++)
				{
					var degrees = a == 0 ? 0f : 45f;
					var inward = new Vector2(MathF.Cos(degrees * MathF.PI / 180f), MathF.Sin(degrees * MathF.PI / 180f));
					var path = Path.Combine(Path.GetTempPath(), $"loom-edgeramp-{backend.Name}-{kind}-{degrees}-{Guid.NewGuid():N}.png");
					// The engine runs with the driver's debug layer on, which reports through LoomLog; an error it raises
					// while drawing these arms fails the test as surely as a wrong ramp.
					var errors = new List<string>();
					var previousSink = LoomLog.Sink;
					LoomLog.Sink = (level, message) =>
					{
						if (level == LoomLogLevel.Error)
							lock (errors) errors.Add(message);
					};
					try
					{
						RenderEdge(backend, path, kind, edge, inward);
						LoomLog.Sink = previousSink;
						foreach (var error in errors)
							failures.Add($"{backend.Name} {name} at {degrees}°: the debug layer reported: {error}");
						var (w, h, rgba) = PngReader.Read(path);
						var ramp = MeasureRamp(rgba, w, h, edge, inward);
						var label = $"{backend.Name} {name} at {degrees}°";

						// Vacuous-pass guards: the edge must actually be there, solid on its inside and clear outside it.
						if (ramp.Partial < 200)
							failures.Add($"{label}: only {ramp.Partial} partly covered pixels along the edge.");
						if (!ramp.InsideSolid || !ramp.OutsideClear)
							failures.Add($"{label}: the edge is not solid inside and clear outside (inside solid {ramp.InsideSolid}, outside clear {ramp.OutsideClear}).");

						widths[a] = ramp.Width;
						if (Math.Abs(ramp.Width - 1.0) > 0.1)
							failures.Add($"{label}: the ramp is {ramp.Width:F3} px wide, not one pixel.");
					}
					finally
					{
						LoomLog.Sink = previousSink;
						if (File.Exists(path)) File.Delete(path);
					}
				}

				if (Math.Abs(widths[1] - widths[0]) > 0.05)
					failures.Add($"{backend.Name} {name}: the ramp is {widths[0]:F3} px wide at 0° but {widths[1]:F3} px at 45°.");
			}
		}

		failures.ShouldBeEmpty("Edge antialiasing depends on the edge's angle:\n  " + string.Join("\n  ", failures));
	}

	// A shape whose one straight edge passes through `edge`, its inside towards `inward`, drawn white through the ui
	// technique as a raw draw list: no compositor, so nothing but the shader decides the coverage. The shape is a square
	// 1200 px across, so its other edges are far outside the target. A rounded rect (kind 1) carries its local rect in uv
	// and params, with a zero radius; a vector triangle (kind 7) carries its signed distance to the edge in Custom.x and
	// leaves the other three distances unused.
	private static void RenderEdge(IRenderBackend backend, string outPath, uint kind, Vector2 edge, Vector2 inward)
	{
		const float Half = 600f;
		using var engine = backend.Create(Surface.Offscreen(W, H), W, H, new EngineSettings());
		UIPainter.Create(engine, new UIHost()); // defines the ui technique; this draws no host
		var white = engine.CreateTexture(TextureData.FromRgba8(1, 1, [255, 255, 255, 255], TextureFormat.Rgba8Unorm, generateMips: false));

		var along = new Vector2(-inward.Y, inward.X);
		var centre = edge + inward * Half;
		var colour = new Half4((Half)1f, (Half)1f, (Half)1f, (Half)1f);
		var vertices = new OverlayVertex[4];
		for (var v = 0; v < 4; v++)
		{
			// Corners (0,0) (1,0) (1,1) (0,1) of the square's local rect, whose u = 0 side is the edge.
			var uv = new Vector2(v is 1 or 2 ? 1f : 0f, v >= 2 ? 1f : 0f);
			var position = centre + inward * (Half * (2f * uv.X - 1f)) + along * (Half * (2f * uv.Y - 1f));
			vertices[v] = kind == 7
				? new OverlayVertex(position, new Vector2(0.5f), colour,
					new Vector4(Vector2.Dot(position - edge, inward), UnusedEdgeDistance, UnusedEdgeDistance, UnusedEdgeDistance))
				: new OverlayVertex(position, uv, colour, new Vector4(0f, 0f, 2f * Half, 2f * Half));
		}
		ushort[] indices = [0, 1, 2, 0, 2, 3];
		DrawCommand[] commands = [new(new Loom.Core.Primitives.Rect(0, 0, W, H), TextureIndex: 0, IndexOffset: 0, IndexCount: 6, VertexOffset: 0, Tag: kind)];
		Texture[] textures = [white];

		engine.SetGraph(RenderGraph.Define(g =>
		{
			var screen = g.Backbuffer;
			g.Pass("edge").Writes(screen).Draw(ctx =>
			{
				// Surface pixels → clip, paper white 1, SDR.
				Span<float> push = [2f / W, 2f / H, -1f, -1f, 1f, 0f];
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

	// The value a vector triangle's unused half-plane slot holds: far enough inside that its coverage is 1.
	private const float UnusedEdgeDistance = 1e20f;

	private readonly record struct Ramp(double Width, int Partial, bool InsideSolid, bool OutsideClear);

	// Fit the ramp width over the middle of the target, clear of its borders: for each partly covered pixel, its centre's
	// signed distance s from the edge and its coverage c (the alpha, which premultiplied alpha-over onto a clear target
	// leaves equal to the coverage) give c − 0.5 = s / width, solved by least squares. Pixels two or more pixels from
	// the edge must be fully covered inside and clear outside.
	private static Ramp MeasureRamp(byte[] rgba, int width, int height, Vector2 edge, Vector2 inward)
	{
		double ss = 0, sc = 0;
		var partial = 0;
		bool insideSolid = true, outsideClear = true;
		for (var y = 64; y < height - 64; y++)
			for (var x = 64; x < width - 64; x++)
			{
				var s = (x + 0.5 - edge.X) * inward.X + (y + 0.5 - edge.Y) * inward.Y;
				var c = rgba[((y * width) + x) * 4 + 3] / 255.0;
				if (s >= 2)
					insideSolid &= c > 0.98;
				else if (s <= -2)
					outsideClear &= c < 0.02;
				else if (c > 0.02 && c < 0.98)
				{
					ss += s * s;
					sc += s * (c - 0.5);
					partial++;
				}
			}
		return new Ramp(sc > 0 ? ss / sc : double.PositiveInfinity, partial, insideSolid, outsideClear);
	}

	/// <summary>
	/// Reversed-Z resolves occlusion identically to a conventional depth buffer. Reversed-Z is three declarations
	/// that have to agree — a depth target cleared to 0, a <see cref="DepthCompare.GreaterOrEqual"/> test, and a
	/// reversed projection — and getting any one wrong doesn't fail, it silently inverts or erases occlusion. So the
	/// invariant is that the two configurations produce the same image: the scene draws a near quad, then a further
	/// one overlapping it, and the depth test is the only thing stopping the second from painting over the first.
	/// </summary>
	[Test]
	public void Reversed_z_resolves_occlusion_the_same_way_as_a_conventional_depth_buffer()
	{
		_ = new VulkanEngineSettings();
		_ = new DirectX12EngineSettings();
		if (Backends.Available.Count == 0)
			throw new TUnit.Core.Exceptions.SkipTestException("no GPU backend available on this box.");

		// Panel-pixel sample points: only the near quad, only the far quad, and where they overlap.
		var nearOnly = new Vector2(W * 0.30f, H * 0.5f);
		var farOnly = new Vector2(W * 0.78f, H * 0.5f);
		var overlap = new Vector2(W * 0.55f, H * 0.5f);

		var failures = new List<string>();
		foreach (var backend in Backends.Available)
		{
			var standardPath = Path.Combine(Path.GetTempPath(), $"loom-depth-std-{backend.Name}-{Guid.NewGuid():N}.png");
			var reversedPath = Path.Combine(Path.GetTempPath(), $"loom-depth-rev-{backend.Name}-{Guid.NewGuid():N}.png");
			try
			{
				RenderOccludedQuads(backend, standardPath, reversed: false);
				RenderOccludedQuads(backend, reversedPath, reversed: true);
				var (w, h, standard) = PngReader.Read(standardPath);
				var (_, _, reversed) = PngReader.Read(reversedPath);

				var diff = ImageComparison.Compare(w, h, standard, reversed, perChannelTolerance: 4);
				if (diff.FailedFraction > 0.005)
					failures.Add($"{backend.Name}: reversed-Z differs from standard-Z — {diff}.");

				// Guard against a vacuous pass: two blank frames also compare equal. The scene must actually show
				// three distinct regions, and the overlap must be the NEAR quad — which is the occlusion itself.
				foreach (var (label, rgba) in new[] { ("standard", standard), ("reversed", reversed) })
				{
					var near = AverageBlock(rgba, w, h, nearOnly, 6);
					var far = AverageBlock(rgba, w, h, farOnly, 6);
					var over = AverageBlock(rgba, w, h, overlap, 6);
					if (MaxChannelDelta(near, far) < 40)
						failures.Add($"{backend.Name}/{label}: the two quads did not render distinguishably (near {Fmt(near)}, far {Fmt(far)}) — nothing to occlude.");
					if (MaxChannelDelta(over, near) > 8)
						failures.Add($"{backend.Name}/{label}: the further quad won the overlap (overlap {Fmt(over)}, near {Fmt(near)}, far {Fmt(far)}).");
				}
			}
			finally
			{
				if (File.Exists(standardPath)) File.Delete(standardPath);
				if (File.Exists(reversedPath)) File.Delete(reversedPath);
			}
		}

		failures.ShouldBeEmpty("Reversed-Z does not match a conventional depth buffer:\n  " + string.Join("\n  ", failures));
	}

	// Two screen-facing quads at different distances, overlapping in the middle, drawn NEAR first so only the depth
	// test can stop the further one painting over it. `mesh` colours by vertex normal, so giving the quads different
	// (geometrically meaningless) normals makes which one won a pixel readable straight out of the image.
	private static void RenderOccludedQuads(IRenderBackend backend, string outPath, bool reversed)
	{
		using var engine = backend.Create(Surface.Offscreen(W, H), W, H, new EngineSettings());
		engine.DefineTechnique("mesh.depth", new TechniqueDescriptor(
			"mesh",
			HasVertexInput: true,
			DepthTest: reversed ? DepthCompare.GreaterOrEqual : DepthCompare.LessOrEqual));

		var nearQuad = Quad(engine, new Vector3(0, 0, 1));  // normal +Z -> blue-ish
		var farQuad = Quad(engine, new Vector3(1, 0, 0));   // normal +X -> red-ish

		var camera = new Camera(engine)
			.Perspective(0.9f, (float)W / H, 0.1f, 100f, reversed)
			.LookAt(new Vector3(0, 0, 6), Vector3.Zero);

		ConstantsHandle viewproj = null!;
		var graph = RenderGraph.Define(g =>
		{
			var screen = g.Backbuffer;
			// The clear value and the compare have to move together; that pairing is what this test exercises.
			var depth = g.Screen.Depth("depth", Format.Depth32F, clearDepth: reversed ? 0f : 1f);
			viewproj = g.Constants<Matrix4x4>("viewproj");

			g.Pass("scene")
				.Reads(viewproj)
				.Writes(screen)
				.WritesDepth(depth)
				.Draw(ctx =>
				{
					DemoDraw.Mesh(ctx, nearQuad, "mesh.depth", Matrix4x4.CreateTranslation(-0.7f, 0, 2f));
					DemoDraw.Mesh(ctx, farQuad, "mesh.depth", Matrix4x4.CreateTranslation(0.7f, 0, -2f));
				});
			g.Present(screen);
		});
		engine.SetGraph(graph);
		engine.Write(viewproj, camera.ViewProjection);

		var inputs = new FrameInputs { Time = 0, DeltaSeconds = 1.0 / 60.0 };
		engine.RequestCapture(outPath);
		engine.Render(inputs);
	}

	// A unit-ish quad in the XY plane carrying `normal` on every vertex — `mesh` shades by normal, so the normal is
	// this quad's identity in the output rather than a lighting input.
	private static Mesh Quad(IEngine engine, Vector3 normal)
	{
		var tangent = new Vector4(1, 0, 0, 1);
		Span<Vertex> vertices =
		[
			new(new Vector3(-1.2f, -1.2f, 0), normal, new Vector2(0, 0), tangent),
			new(new Vector3(1.2f, -1.2f, 0), normal, new Vector2(1, 0), tangent),
			new(new Vector3(1.2f, 1.2f, 0), normal, new Vector2(1, 1), tangent),
			new(new Vector3(-1.2f, 1.2f, 0), normal, new Vector2(0, 1), tangent),
		];
		Span<uint> indices = [0, 1, 2, 0, 2, 3];
		return engine.CreateMesh(vertices, indices);
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
