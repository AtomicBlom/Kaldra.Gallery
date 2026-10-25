using System.Numerics;

using Loom.Assets;
using Loom.Core.Hosting;
using Loom.Core.Materials;
using Loom.Gallery.Demos;
using Loom.UI.SUIsei;

using SUIsei;

namespace Loom.Tests;

/// <summary>
/// Renders a <see cref="DemoGallery"/> demo offscreen at a frozen, deterministic pose — the in-process replacement
/// for the sandbox's old <c>--capture</c> mode that fed the golden/parity harness. Brings an engine up on an
/// offscreen surface, builds the shared <see cref="DemoScene"/> chrome-free (an empty UI host, no ImGui overlay),
/// drives the demo through the same per-frame path the host would (<c>Update</c>/<c>PreFrame</c>), and grabs one
/// frame after the delta-driven state (auto-exposure, particles) has converged. Frozen time + a fixed delta make a
/// re-run reproduce the same image, and make two backends' captures directly comparable.
/// </summary>
internal static class GalleryCapture
{
	public const int Width = 1280;
	public const int Height = 720;

	// Frozen scene time with a real per-frame delta: the shader clock is constant while delta-driven simulation
	// still advances a fixed amount each frame, so auto-exposure and particle systems converge deterministically.
	private const float CaptureTime = 1.0f;
	private const double CaptureDelta = 1.0 / 60.0;
	private const int Frames = 62;       // enough for auto-exposure / particle convergence
	private const int CaptureFrame = 60; // grab late, then flush a couple frames so the async readback completes

	private static string AssetsDir => Path.Combine(AppContext.BaseDirectory, "assets");

	/// <summary>
	/// Render <paramref name="demoFactory"/>'s graph on <paramref name="backend"/> to <paramref name="outPngPath"/>
	/// (via <see cref="IEngine.RequestCapture"/>, the same PNG encoder a windowed capture uses). Returns false —
	/// writing nothing — if the demo's techniques don't cross-compile on this backend/toolchain
	/// (<see cref="IDemo.DefineTechniques"/> throws <see cref="NotSupportedException"/>), the same capability filter
	/// the gallery applies, so a demo missing on one backend is skipped rather than failing the run.
	/// <paramref name="settings"/> overrides the engine configuration (e.g. force a software adapter for a GPU-less
	/// WARP capture); when null, stock <see cref="EngineSettings"/> is used.
	/// </summary>
	public static bool TryRender(IRenderBackend backend, Func<IDemo> demoFactory, string outPngPath, EngineSettings? settings = null)
	{
		using var engine = backend.Create(Surface.Offscreen(Width, Height), Width, Height, settings ?? new EngineSettings());

		DemoGraph.Overlay = null; // chrome-free: no Dear ImGui overlay in the hud pass

		var demo = demoFactory();
		try
		{
			demo.DefineTechniques(engine);
		}
		catch (ShaderAssetMissingException)
		{
			return false; // a stage this toolchain emitted no bytecode for — nothing to compare, not a failure
		}

		var uiHost = new UIHost();
		var scene = BuildScene(engine, uiHost);

		// Preload assets (if any), draining the engine's upload queue on this thread since no frame loop runs yet.
		var preload = demo.PreloadAsync(scene);
		while (!preload.IsCompleted)
			engine.ServicePendingUploads();
		preload.GetAwaiter().GetResult();

		engine.SetGraph(demo.Build(scene));

		var aspect = (float)Width / Height;
		var orbit = new Loom.Concepts.Camera(engine);
		DemoCamera.Orbit(orbit, CaptureTime, aspect);
		var camera = demo.GetCamera(aspect, CaptureTime) ?? orbit;

		var surfaceSize = new Vector2(Width, Height);
		var inputs = new FrameInputs { Time = CaptureTime, DeltaSeconds = CaptureDelta };
		for (var frame = 1; frame <= Frames; frame++)
		{
			demo.Update(camera);
			demo.PreFrame(CaptureDelta, CaptureTime, camera, surfaceSize);
			uiHost.Tick(CaptureDelta, CaptureTime, surfaceSize); // empty host → the hud pass composites nothing
			uiHost.Render();
			scene.UI.Prepare();
			if (frame == CaptureFrame)
				engine.RequestCapture(outPngPath);
			engine.Render(inputs);
		}
		return true;
	}

	/// <summary>
	/// Render <paramref name="demoFactory"/> twice on <b>one</b> engine, from two separately-built graphs, and
	/// capture the second — the offscreen stand-in for a running app switching demos. The frame it produces must
	/// match <see cref="TryRender"/>'s, because building a second graph is supposed to be invisible in the output.
	/// <para>
	/// What this actually exercises is cache invalidation across a graph swap. A backend that caches anything keyed
	/// on a transient's GPU objects — Vulkan caches descriptor sets by the image views they name — has to drop that
	/// cache when the outgoing graph's transients are destroyed. Nothing else in the suite swaps a graph on a live
	/// engine, so without this a stale-cache bug is invisible until someone changes demos in the sandbox.
	/// </para>
	/// </summary>
	public static bool TryRenderAfterGraphSwap(IRenderBackend backend, Func<IDemo> demoFactory, string outPngPath, EngineSettings? settings = null)
	{
		using var engine = backend.Create(Surface.Offscreen(Width, Height), Width, Height, settings ?? new EngineSettings());

		DemoGraph.Overlay = null;

		var demo = demoFactory();
		try
		{
			demo.DefineTechniques(engine);
		}
		catch (ShaderAssetMissingException)
		{
			return false;
		}

		var uiHost = new UIHost();
		var scene = BuildScene(engine, uiHost);

		var preload = demo.PreloadAsync(scene);
		while (!preload.IsCompleted)
			engine.ServicePendingUploads();
		preload.GetAwaiter().GetResult();

		var aspect = (float)Width / Height;
		var orbit = new Loom.Concepts.Camera(engine);
		DemoCamera.Orbit(orbit, CaptureTime, aspect);
		var camera = demo.GetCamera(aspect, CaptureTime) ?? orbit;
		var surfaceSize = new Vector2(Width, Height);
		var inputs = new FrameInputs { Time = CaptureTime, DeltaSeconds = CaptureDelta };

		// The first graph, driven just far enough to bring its transients up and warm any per-frame cache against
		// them — both frame slots, plus one. Deliberately not the full capture sequence: delta-driven state
		// (auto-exposure, particles) advances with every frame recorded, and a long warm-up would leave the second
		// pass converged further than a cold render, which shows up as a ±1 LSB difference that has nothing to do
		// with what this is checking.
		const int WarmUpFrames = 3;
		engine.SetGraph(demo.Build(scene));
		for (var frame = 1; frame <= WarmUpFrames; frame++)
		{
			demo.Update(camera);
			demo.PreFrame(CaptureDelta, CaptureTime, camera, surfaceSize);
			uiHost.Tick(CaptureDelta, CaptureTime, surfaceSize);
			uiHost.Render();
			scene.UI.Prepare();
			engine.Render(inputs);
		}

		// A second Build is a distinct RenderGraph, so the backend reallocates every transient and the first graph's
		// images and views are destroyed. Then render the identical sequence again and capture that.
		engine.SetGraph(demo.Build(scene));
		for (var frame = 1; frame <= Frames; frame++)
		{
			demo.Update(camera);
			demo.PreFrame(CaptureDelta, CaptureTime, camera, surfaceSize);
			uiHost.Tick(CaptureDelta, CaptureTime, surfaceSize);
			uiHost.Render();
			scene.UI.Prepare();
			if (frame == CaptureFrame)
				engine.RequestCapture(outPngPath);
			engine.Render(inputs);
		}
		return true;
	}

	/// <summary>
	/// The shared scene every demo draws against: cube/plane/quad geometry, the brick albedo+normal material, and a
	/// UI painter over an empty host (so the demos' hud pass composites nothing — the chrome-free capture). Shared
	/// with the headless workloads that drive a demo's frame loop themselves rather than through
	/// <see cref="TryRender"/>.
	/// </summary>
	public static DemoScene BuildScene(IEngine engine, UIHost uiHost)
	{
		var (cubeVerts, cubeIndices) = Geometry.BuildCube();
		var (planeVerts, planeIndices) = Geometry.BuildPlane();
		var (quadVerts, quadIndices) = Geometry.BuildQuad();
		var brick = engine.CreateMaterial(
			engine.CreateTexture(ImageLoader.Load(Path.Combine(AssetsDir, "brick_albedo.png"), TextureFormat.Rgba8Srgb)),
			engine.CreateTexture(ImageLoader.Load(Path.Combine(AssetsDir, "brick_normal.png"), TextureFormat.Rgba8Unorm)));
		var uiPainter = UIPainter.Create(engine, uiHost, FontAtlas.Default);
		return new DemoScene(
			engine.CreateMesh(cubeVerts, cubeIndices),
			engine.CreateMesh(planeVerts, planeIndices),
			engine.CreateMesh(quadVerts, quadIndices),
			brick, uiPainter, name => File.ReadAllBytesAsync(Path.Combine(AssetsDir, name)));
	}
}
