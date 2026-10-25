using System.Diagnostics;
using System.Numerics;

using Loom.Backend.DirectX12;
using Loom.Backend.Vulkan;
using Loom.Core.Hosting;
using Loom.Gallery.Demos;

using SUIsei;

namespace Loom.Tests;

/// <summary>
/// The draw-throughput floor: a frame made of thousands of <em>discrete</em> draws must still cost nothing in
/// managed memory, and must cost a number somebody can read. Every other dense workload in the suite is one
/// instanced draw, which measures the GPU; this one measures what a draw call costs the CPU, which is the thing that
/// decides whether an engine can put ten thousand distinct objects on screen.
/// <para>
/// The allocation assertion is the gate — a per-draw allocation is invisible at demo scale and turns into GC pauses
/// exactly when a scene gets big, so it is asserted at zero rather than at a threshold. The wall-clock and GPU
/// numbers are <em>reported, never asserted</em>: they vary by machine, driver, and thermal state, and a
/// perf-threshold test that fails on a hot laptop teaches people to ignore the suite.
/// </para>
/// GPU-gated — SKIPPED when no backend is available.
/// </summary>
[NotInParallel] // one GPU engine at a time, and the allocation counter is per-thread
public class DrawScaleTests
{
	private const int W = 640, H = 480;

	// How many discrete draws the measured frame issues — the number the milestone is named for. It lives here
	// rather than in the demo so the headless gate and the interactive demo can differ.
	private const int DrawCount = 10_000;

	private const int WarmupFrames = 20;   // pipeline bakes, descriptor-pool growth, first-frame ring sizing
	private const int MeasuredFrames = 30;
	private const int TimedFrames = 60;

	// A real lattice of shaded cubes renders thousands of distinct colours; a frame that threw, cleared, or drew
	// nothing collapses toward one. Without this the allocation assertion would pass vacuously on a black frame.
	private const int MinDistinctColors = 64;

	[Test]
	public void A_dense_discrete_draw_workload_allocates_nothing_per_frame()
	{
		_ = new VulkanEngineSettings();
		_ = new DirectX12EngineSettings();
		if (Backends.Available.Count == 0)
			throw new TUnit.Core.Exceptions.SkipTestException("No GPU backend is available on this box (Backends.Available is empty).");

		var failures = new List<string>();
		var measured = 0;

		foreach (var backend in Backends.Available)
		{
			DemoGraph.Overlay = null; // chrome-free: no Dear ImGui overlay in the hud pass

			using var engine = backend.Create(Surface.Offscreen(W, H), W, H, new EngineSettings());
			var demo = new ScaleDemo(DrawCount);
			try
			{
				demo.DefineTechniques(engine);
			}
			catch (ShaderAssetMissingException)
			{
				continue; // a stage this toolchain emitted no bytecode for — the gallery's own capability filter
			}

			var uiHost = new UIHost();
			var scene = GalleryCapture.BuildScene(engine, uiHost);
			engine.SetGraph(demo.Build(scene));

			// Everything per-frame the *app* would do happens once, before the measurement: the camera is fixed, so
			// the view-projection it publishes never changes, and an empty UI host composites nothing. What the
			// counter then sees across Render is the engine's own frame path and nothing else.
			var camera = demo.GetCamera((float)W / H, 1f)!;
			demo.Update(camera);
			uiHost.Tick(1.0 / 60.0, 1f, new Vector2(W, H));
			uiHost.Render();
			scene.UI.Prepare();

			var inputs = new FrameInputs { Time = 1.0, DeltaSeconds = 1.0 / 60.0 };
			for (var frame = 0; frame < WarmupFrames; frame++)
				engine.Render(inputs);

			var before = GC.GetAllocatedBytesForCurrentThread();
			for (var frame = 0; frame < MeasuredFrames; frame++)
				engine.Render(inputs);
			var allocated = GC.GetAllocatedBytesForCurrentThread() - before;

			if (allocated != 0)
				failures.Add($"{backend.Name}: {allocated} bytes over {MeasuredFrames} warm frames of {DrawCount} draws " +
					$"({allocated / (double)MeasuredFrames:0.#} per frame, {allocated / (double)MeasuredFrames / DrawCount:0.##} per draw).");

			Report(backend.Name, engine, inputs);

			var distinct = CaptureDistinctColors(engine, inputs);
			if (distinct < MinDistinctColors)
				failures.Add($"{backend.Name}: the measured frame was near-empty ({distinct} distinct colours) — " +
					"the allocation result would have been vacuous.");
			measured++;
		}

		measured.ShouldBeGreaterThan(0, "No backend rendered the scale workload, so nothing was measured.");
		failures.ShouldBeEmpty("The discrete-draw workload did not hold the hot-path floor:\n  " + string.Join("\n  ", failures));
	}

	// Wall-clock per frame, plus the GPU's own per-pass timestamps where the backend can supply them. Timing is a
	// separate loop from the allocation loop on purpose: resolving timestamps allocates, and the pillar assertion
	// must not be measuring the measurement.
	private static void Report(string backend, IEngine engine, FrameInputs inputs)
	{
		var clock = Stopwatch.StartNew();
		for (var frame = 0; frame < TimedFrames; frame++)
			engine.Render(inputs);
		clock.Stop();
		var cpuMs = clock.Elapsed.TotalMilliseconds / TimedFrames;

		// Enabling is self-gating: a device or queue that can't take timestamps leaves the flag false, so reading it
		// back is the capability check.
		var gpu = "n/a";
		engine.GpuTimingEnabled = true;
		if (engine.GpuTimingEnabled)
		{
			for (var frame = 0; frame < WarmupFrames; frame++)
				engine.Render(inputs);
			gpu = $"{engine.LastFrameGpuMilliseconds:0.###} ms";
			engine.GpuTimingEnabled = false;
		}

		// The configuration is part of the number: a Debug run carries unelided bounds checks and no inlining, so a
		// Debug figure compared against a Release one reads as a regression that never happened.
#if DEBUG
		const string config = "Debug";
#else
		const string config = "Release";
#endif
		Console.WriteLine($"[scale] {backend} ({config}): {DrawCount} discrete draws — {cpuMs:0.###} ms/frame CPU " +
			$"({cpuMs * 1000.0 / DrawCount:0.###} µs/draw), GPU {gpu}.");
	}

	// Grab a frame through the same PNG path a windowed capture uses, then count what it actually drew.
	private static int CaptureDistinctColors(IEngine engine, FrameInputs inputs)
	{
		var path = Path.Combine(Path.GetTempPath(), $"loom-scale-{Guid.NewGuid():N}.png");
		try
		{
			engine.RequestCapture(path);
			for (var frame = 0; frame < 3; frame++) // request, then flush so the async readback lands
				engine.Render(inputs);

			var (_, _, rgba) = PngReader.Read(path);
			return ImageComparison.DistinctColors(rgba);
		}
		finally
		{
			if (File.Exists(path))
				File.Delete(path);
		}
	}
}
