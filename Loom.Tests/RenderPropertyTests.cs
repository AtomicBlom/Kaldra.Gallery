using System.Numerics;

using Loom.Backend.DirectX12;
using Loom.Backend.Vulkan;
using Loom.Concepts;
using Loom.Core.Geometry;
using Loom.Core.Graph;
using Loom.Core.Hosting;
using Loom.Core.Materials;
using Loom.Gallery.Demos;
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
