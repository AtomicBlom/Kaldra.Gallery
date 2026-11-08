using Loom.Backend.DirectX12;
using Loom.Backend.Vulkan;
using Loom.Core.Diagnostics;
using Loom.Core.Graph;
using Loom.Core.Hosting;
using Loom.Core.Materials;
using Loom.Gallery.Demos;
using Loom.Graph;

namespace Loom.Tests;

/// <summary>
/// Multisampling and its derived resolve, as a property: the MSAA demo drawn with four samples a pixel and then with one
/// (its control) shows the same scene, but the four-sample frame's edges — the cube's, and the grille's alpha-to-coverage
/// bars — are blended, so it has many more pixels part way between their neighbours. A resolve that never ran would
/// sample nothing (a blank frame); one that sampled a single sample would match the control. The frames are kept when
/// <c>LOOM_KEEP_CAPTURES</c> is set. GPU-gated — SKIPPED when no backend is available.
/// <para>
/// The rest hold the resolve's ordering against full-screen fills, whose four samples a pixel are all alike and so resolve
/// to exactly what one sample draws: passes drawing and blending one after another into a sampled target, a target
/// sampled, drawn again and sampled again, and two passes sampling it between drawings. And what has no resolve is
/// refused when the graph is derived.
/// </para>
/// </summary>
[NotInParallel] // one GPU engine at a time
public class MsaaTests
{
	[Test]
	public void Four_samples_blend_the_edges_one_sample_steps()
	{
		_ = new VulkanEngineSettings();
		_ = new DirectX12EngineSettings();
		if (Backends.Available.Count == 0)
			throw new TUnit.Core.Exceptions.SkipTestException("No GPU backend is available on this box (Backends.Available is empty).");
		var keep = Environment.GetEnvironmentVariable("LOOM_KEEP_CAPTURES") is not null;

		foreach (var backend in Backends.Available)
		{
			var four = Path.Combine(Path.GetTempPath(), $"loom-msaa-{backend.Name}-4x.png");
			var one = Path.Combine(Path.GetTempPath(), $"loom-msaa-{backend.Name}-1x.png");
			try
			{
				if (!GalleryCapture.TryRender(backend, () => new MsaaDemo(4), four, withUI: false)
					|| !GalleryCapture.TryRender(backend, () => new MsaaDemo(1), one, withUI: false))
					continue;
				var (w, h, msaa) = PngReader.Read(four);
				var (_, _, plain) = PngReader.Read(one);
				var blended = Blended(w, h, msaa);
				var stepped = Blended(w, h, plain);
				// A stepped edge leaves next to no pixel part way between its neighbours; a blended one leaves a line of them
				blended.ShouldBeGreaterThan(1000, $"{backend.Name}: four samples left only {blended} blended edge pixels; the edges weren't resolved.");
				blended.ShouldBeGreaterThan(stepped * 3, $"{backend.Name}: four samples blended {blended} edge pixels against one sample's {stepped}.");
			}
			finally
			{
				if (!keep)
				{
					File.Delete(four);
					File.Delete(one);
				}
			}
		}
	}

	/// <summary>
	/// Passes drawing one after another into a multisampled target that is then sampled each see what the one before
	/// drew: a red fill written, then green and blue added over it, sampled into the frame, which shows all three. The
	/// same graph at one sample a pixel is the control, since full-screen fills cover every sample alike.
	/// </summary>
	[Test]
	public void Writes_then_blends_into_a_sampled_target_all_reach_its_resolve() =>
		MatchesOneSample("write-blend", (g, msaa) =>
		{
			var scene = msaa.Color("scene", Format.Rgba16F);
			g.Pass("red").Writes(scene).Draw(ctx => ctx.FullScreen("probe.opaque", Red));
			g.Pass("green").Blends(scene).Draw(ctx => ctx.FullScreen("probe.add", Green));
			g.Pass("blue").Blends(scene).Draw(ctx => ctx.FullScreen("probe.add", Blue));
			g.Pass("show").Samples(scene).Writes(g.Backbuffer).Draw(ctx => ctx.FullScreen("blit", scene));
			g.Present(g.Backbuffer);
		}, (r, gr, b) => r > Lit && gr > Lit && b > Lit ? null : "red, green and blue should all show");

	/// <summary>
	/// A target drawn, sampled, drawn again and sampled again is resolved twice, and the second sampling sees the second
	/// drawing: red, sampled into one copy, then green over it, sampled into another, the two copies added in the frame.
	/// A resolve that ran once, or before the second drawing, leaves no green.
	/// </summary>
	[Test]
	public void A_target_drawn_again_after_it_is_sampled_resolves_again() =>
		MatchesOneSample("redraw", (g, msaa) =>
		{
			var scene = msaa.Color("scene", Format.Rgba16F);
			var first = g.Screen.Color("first", Format.Rgba16F);
			var second = g.Screen.Color("second", Format.Rgba16F);
			g.Pass("red").Writes(scene).Draw(ctx => ctx.FullScreen("probe.opaque", Red));
			g.Pass("copy-red").Samples(scene).Writes(first).Draw(ctx => ctx.FullScreen("blit", scene));
			g.Pass("green").Writes(scene).Draw(ctx => ctx.FullScreen("probe.opaque", Green));
			g.Pass("copy-green").Samples(scene).Writes(second).Draw(ctx => ctx.FullScreen("blit", scene));
			g.Pass("show-first").Samples(first).Writes(g.Backbuffer).Draw(ctx => ctx.FullScreen("blit", first));
			g.Pass("show-second").Samples(second).Blends(g.Backbuffer).Draw(ctx => ctx.FullScreen("blit.add", second));
			g.Present(g.Backbuffer);
		}, (r, gr, b) => r > Lit && gr > Lit && b < Dark ? null : "the first copy should be red and the second green");

	/// <summary>
	/// Two passes sampling a multisampled target between the same two drawings share one resolve, and both read it: each
	/// copies it, and the frame adds the copies.
	/// </summary>
	[Test]
	public void Two_passes_sampling_a_target_share_its_resolve() =>
		MatchesOneSample("two-readers", (g, msaa) =>
		{
			var scene = msaa.Color("scene", Format.Rgba16F);
			var first = g.Screen.Color("first", Format.Rgba16F);
			var second = g.Screen.Color("second", Format.Rgba16F);
			g.Pass("fill").Writes(scene).Draw(ctx => ctx.FullScreen("probe.opaque", Yellow));
			g.Pass("reader-1").Samples(scene).Writes(first).Draw(ctx => ctx.FullScreen("blit", scene));
			g.Pass("reader-2").Samples(scene).Writes(second).Draw(ctx => ctx.FullScreen("blit", scene));
			g.Pass("show-first").Samples(first).Writes(g.Backbuffer).Draw(ctx => ctx.FullScreen("blit", first));
			g.Pass("show-second").Samples(second).Blends(g.Backbuffer).Draw(ctx => ctx.FullScreen("blit.add", second));
			g.Present(g.Backbuffer);
		}, (r, gr, b) => r > Lit && gr > Lit && b < Dark ? null : "both copies should be yellow");

	/// <summary>
	/// What a multisampled target can't be is refused when the graph is derived, the same for every backend and before
	/// any allocates it: read back, read-written as storage, presented, persistent, or (a depth target) sampled.
	/// </summary>
	[Test]
	public void Uses_of_a_multisampled_target_with_no_resolve_are_refused_at_derive_time()
	{
		static void Refused(string expected, Action<GraphBuilder, Domain> define)
		{
			var thrown = Should.Throw<GraphValidationException>(() => GraphSchedule.Derive(RenderGraph.Define(g => define(g, g.Domain("msaa", 64, 64, 4)))));
			thrown.Message.ShouldContain("is multisampled (4x, domain 'msaa')");
			thrown.Message.ShouldContain(expected);
		}

		Refused("is read back", (g, msaa) =>
		{
			var color = msaa.Color("color", Format.Rgba8Unorm);
			g.Pass("draw").Writes(color).Draw(_ => { });
			g.Readback(color);
		});
		Refused(".ReadWrites", (g, msaa) =>
		{
			var color = msaa.Color("color", Format.Rgba8Unorm);
			g.Compute("storage").ReadWrites(color).Dispatch(_ => { });
		});
		Refused("is a depth target pass 'sample' samples", (g, msaa) =>
		{
			var depth = msaa.Depth("depth", Format.Depth32F);
			var color = g.Screen.Color("color", Format.Rgba8Unorm);
			g.Pass("draw").WritesDepth(depth).Draw(_ => { });
			g.Pass("sample").Samples(depth).Writes(color).Draw(_ => { });
		});
		Refused("is presented", (g, msaa) =>
		{
			var color = msaa.Color("color", Format.Rgba8Unorm);
			g.Pass("draw").Writes(color).Draw(_ => { });
			g.Present(color);
		});
		Refused("is persistent", (g, msaa) =>
		{
			_ = msaa.Persistent("history", Format.Rgba8Unorm);
			var color = msaa.Color("color", Format.Rgba8Unorm);
			g.Pass("draw").Writes(color).Draw(_ => { });
		});

		// What it can be: drawn into, blended over, depth-tested, and (colour) sampled
		Should.NotThrow(() => GraphSchedule.Derive(RenderGraph.Define(g =>
		{
			var msaa = g.Domain("msaa", 64, 64, 4);
			var color = msaa.Color("color", Format.Rgba8Unorm);
			var depth = msaa.Depth("depth", Format.Depth32F);
			g.Pass("draw").Writes(color).WritesDepth(depth).Draw(_ => { });
			g.Pass("over").Blends(color).ReadsDepth(depth).Draw(_ => { });
			g.Pass("show").Samples(color).Writes(g.Backbuffer).Draw(_ => { });
			g.Present(g.Backbuffer);
		})));
	}

	private static readonly float[] Red = [0.4f, 0f, 0f, 1f];
	private static readonly float[] Green = [0f, 0.4f, 0f, 1f];
	private static readonly float[] Blue = [0f, 0f, 0.4f, 1f];
	private static readonly float[] Yellow = [0.2f, 0.2f, 0f, 1f];

	// A channel of the frame's sRGB bytes that a probe fill lit, and one it left dark, at the right of the frame
	private const int Lit = 64, Dark = 16;

	private const int W = 256, H = 128;

	// Render `define` with four samples a pixel and with one on every backend, and hold the four-sample frame to the
	// one-sample frame, to `content` (given the red, green and blue at the right of the frame, where the probe's
	// left-to-right ramp is brightest; null if it is right) and to the debug layer's silence
	private static void MatchesOneSample(string name, Action<GraphBuilder, Domain> define, Func<int, int, int, string?> content)
	{
		_ = new VulkanEngineSettings();
		_ = new DirectX12EngineSettings();
		if (Backends.Available.Count == 0)
			throw new TUnit.Core.Exceptions.SkipTestException("No GPU backend is available on this box (Backends.Available is empty).");

		var rendered = 0;
		var failures = new List<string>();
		foreach (var backend in Backends.Available)
		{
			var (four, fourErrors) = Render(backend, 4, define, Path.Combine(Path.GetTempPath(), $"loom-msaa-{name}-{backend.Name}-4x-{Guid.NewGuid():N}.png"));
			var (one, _) = Render(backend, 1, define, Path.Combine(Path.GetTempPath(), $"loom-msaa-{name}-{backend.Name}-1x-{Guid.NewGuid():N}.png"));
			rendered++;
			foreach (var error in fourErrors)
				failures.Add($"{backend.Name}: the debug layer reported: {error}");

			var i = (((H / 2) * W) + (W - 6)) * 4;
			if (content(four[i], four[i + 1], four[i + 2]) is { } wrong)
				failures.Add($"{backend.Name}: four samples drew rgb({four[i]}, {four[i + 1]}, {four[i + 2]}) at the right of the frame; {wrong}.");
			if (content(one[i], one[i + 1], one[i + 2]) is { } controlWrong)
				failures.Add($"{backend.Name}: the one-sample control drew rgb({one[i]}, {one[i + 1]}, {one[i + 2]}); {controlWrong}.");
			var diff = ImageComparison.Compare(W, H, four, one, 3);
			if (diff.FailedFraction > 0.001)
				failures.Add($"{backend.Name}: four samples differ from one: {diff}");
		}

		rendered.ShouldBeGreaterThan(0, "no backend rendered the graph.");
		failures.ShouldBeEmpty($"{name}:\n  " + string.Join("\n  ", failures));
	}

	// The graph `define` builds over a domain of `samples` a pixel, rendered offscreen and captured on its second frame,
	// with every error the debug layer reported meanwhile
	private static (byte[] Rgba, List<string> Errors) Render(IRenderBackend backend, int samples, Action<GraphBuilder, Domain> define, string path)
	{
		var errors = new List<string>();
		var previousSink = LoomLog.Sink;
		LoomLog.Sink = (level, message) =>
		{
			if (level == LoomLogLevel.Error)
				lock (errors) errors.Add(message);
		};
		try
		{
			using (var engine = backend.Create(Surface.Offscreen(W, H), W, H, new EngineSettings()))
			{
				engine.DefineTechnique("probe.opaque", new TechniqueDescriptor("probe", HasFragmentPush: true));
				engine.DefineTechnique("probe.add", new TechniqueDescriptor("probe", HasFragmentPush: true, Blend: BlendMode.Additive));
				engine.DefineTechnique("blit", new TechniqueDescriptor("blit"));
				engine.DefineTechnique("blit.add", new TechniqueDescriptor("blit", Blend: BlendMode.Additive));
				engine.SetGraph(RenderGraph.Define(g => define(g, g.Domain("msaa", W, H, samples))));
				var inputs = new FrameInputs { Time = 0, DeltaSeconds = 1.0 / 60.0 };
				for (var frame = 1; frame <= 3; frame++)
				{
					if (frame == 2)
						engine.RequestCapture(path);
					engine.Render(inputs);
				}
			}
			var (w, h, rgba) = PngReader.Read(path);
			(w, h).ShouldBe((W, H));
			return (rgba, errors);
		}
		finally
		{
			LoomLog.Sink = previousSink;
			File.Delete(path);
		}
	}

	// Pixels that lie part way between the neighbours either side of them, horizontally or vertically, by a margin: what
	// an edge blended across its pixel leaves, and a stepped one doesn't
	private static int Blended(int width, int height, byte[] rgba)
	{
		int Luma(int x, int y)
		{
			var i = ((y * width) + x) * 4;
			return (rgba[i] * 3) + (rgba[i + 1] * 6) + rgba[i + 2];
		}

		var count = 0;
		for (var y = 1; y < height - 1; y++)
			for (var x = 1; x < width - 1; x++)
			{
				int c = Luma(x, y);
				if (Between(Luma(x - 1, y), c, Luma(x + 1, y)) || Between(Luma(x, y - 1), c, Luma(x, y + 1)))
					count++;
			}
		return count;
	}

	private static bool Between(int a, int c, int b)
	{
		int low = Math.Min(a, b), high = Math.Max(a, b);
		return high - low > 300 && c > low + 60 && c < high - 60;
	}
}
