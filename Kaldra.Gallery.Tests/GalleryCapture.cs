using System.Numerics;

using BinaryVibrance.Kaldra.Assets;
using BinaryVibrance.Kaldra.Core.Hosting;
using BinaryVibrance.Kaldra.Core.Materials;
using Kaldra.Gallery.Demos;
using BinaryVibrance.Kaldra.UI.Vysara;

using BinaryVibrance.Vysara;
using BinaryVibrance.Vysara.Input;

namespace BinaryVibrance.Kaldra.Tests;

/// <summary>
/// Renders a <see cref="DemoGallery"/> demo offscreen at a frozen, deterministic pose — the in-process replacement
/// for the sandbox's old <c>--capture</c> mode that fed the golden/parity harness. Brings an engine up on an
/// offscreen surface, builds the shared <see cref="DemoScene"/> chrome-free (no shell bars or debug panel, no ImGui
/// overlay — but a UI demo's own page, as the browser's capture shows it), drives the demo through the same per-frame
/// path the host would (<c>Update</c>/<c>PreFrame</c>, plus the demo's scripted click), and grabs one frame after the
/// delta-driven state (auto-exposure, particles) has converged. Frozen time + a fixed delta make a re-run reproduce the
/// same image, and make two backends' captures directly comparable.
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
										 // The frame a demo's scripted click lands on — the browser capture's, so the two show the same response to it.
	private const int CaptureClickFrame = 30;

	private static string AssetsDir => Path.Combine(AppContext.BaseDirectory, "assets");

	/// <summary>
	/// Render <paramref name="demoFactory"/>'s graph on <paramref name="backend"/> to <paramref name="outPngPath"/>
	/// (via <see cref="IEngine.RequestCapture"/>, the same PNG encoder a windowed capture uses). Returns false —
	/// writing nothing — if the demo's techniques don't cross-compile on this backend/toolchain
	/// (<see cref="IDemo.DefineTechniques"/> throws <see cref="NotSupportedException"/>), the same capability filter
	/// the gallery applies, so a demo missing on one backend is skipped rather than failing the run.
	/// <paramref name="settings"/> overrides the engine configuration (e.g. force a software adapter for a GPU-less
	/// WARP capture); when null, stock <see cref="EngineSettings"/> is used.
	/// <paramref name="withUI"/> false renders the demo without any UI of its own — no page, no scripted click, and no
	/// <see cref="IDemo.PreFrame"/>, which is where a demo composites a UI it owns (an in-world panel) — so a test can
	/// show that the UI changes the frame rather than two blank UIs comparing equal.
	/// </summary>
	public static bool TryRender(IRenderBackend backend, Func<IDemo> demoFactory, string outPngPath, EngineSettings? settings = null, bool withUI = true)
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
		if (withUI)
			ShowDemoUI(uiHost, demo);

		// Preload assets (if any), draining the engine's upload queue on this thread since no frame loop runs yet.
		var preload = demo.PreloadAsync(scene);
		while (!preload.IsCompleted)
			engine.ServicePendingUploads();
		preload.GetAwaiter().GetResult();

		engine.SetGraph(demo.Build(scene));
		RunFrames(engine, demo, scene, uiHost, CaptureCamera(engine, demo), Frames, outPngPath, withUI);
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
		ShowDemoUI(uiHost, demo);

		var preload = demo.PreloadAsync(scene);
		while (!preload.IsCompleted)
			engine.ServicePendingUploads();
		preload.GetAwaiter().GetResult();

		var camera = CaptureCamera(engine, demo);

		// The first graph, driven just far enough to bring its transients up and warm any per-frame cache against
		// them — both frame slots, plus one. Deliberately not the full capture sequence: delta-driven state
		// (auto-exposure, particles) advances with every frame recorded, and a long warm-up would leave the second
		// pass converged further than a cold render, which shows up as a ±1 LSB difference that has nothing to do
		// with what this is checking. It also stops short of the scripted click, which the capture pass then makes
		// exactly once, as TryRender does.
		const int WarmUpFrames = 3;
		engine.SetGraph(demo.Build(scene));
		RunFrames(engine, demo, scene, uiHost, camera, WarmUpFrames, capturePath: null, withUI: true);

		// A second Build is a distinct RenderGraph, so the backend reallocates every transient and the first graph's
		// images and views are destroyed. Then render the identical sequence again and capture that.
		engine.SetGraph(demo.Build(scene));
		RunFrames(engine, demo, scene, uiHost, camera, Frames, outPngPath, withUI: true);
		return true;
	}

	/// <summary>
	/// Show <paramref name="demo"/>'s own UI on <paramref name="uiHost"/>, chrome-free: the shared <see cref="DemoShell"/>
	/// with its bars and debug panel hidden and the demo's <see cref="IDemo.OverlayContent"/> centred — what the browser
	/// host shows in capture mode, so a desktop capture and a browser capture of a UI demo compose the same tree. A demo
	/// with no page of its own leaves the shell empty, and its hud pass draws nothing.
	/// </summary>
	public static void ShowDemoUI(UIHost uiHost, IDemo demo)
	{
		var shell = new DemoShell([demo.Name], _ => { });
		shell.SetHudVisible(false);
		shell.SetCenterContent(demo.OverlayContent);
		uiHost.CreateRoot(UILayer.Default).Root.Content = shell.Root;
	}

	/// <summary>
	/// The shared scene every demo draws against: cube/plane/quad geometry, the brick albedo+normal material, and a
	/// UI painter over <paramref name="uiHost"/> (empty until <see cref="ShowDemoUI"/> gives it the demo's page). Shared
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
		var uiPainter = UIPainter.Create(engine, uiHost);
		return new DemoScene(
			engine.CreateMesh(cubeVerts, cubeIndices),
			engine.CreateMesh(planeVerts, planeIndices),
			engine.CreateMesh(quadVerts, quadIndices),
			brick, uiPainter, name => File.ReadAllBytesAsync(Path.Combine(AssetsDir, name)));
	}

	// The frozen pose a capture renders from: the demo's own camera, else the shared orbit at the capture time.
	private static BinaryVibrance.Kaldra.Concepts.Camera CaptureCamera(IEngine engine, IDemo demo)
	{
		var aspect = (float)Width / Height;
		var orbit = new BinaryVibrance.Kaldra.Concepts.Camera(engine);
		DemoCamera.Orbit(orbit, CaptureTime, aspect);
		return demo.GetCamera(aspect, CaptureTime) ?? orbit;
	}

	// Drive the demo through `frames` frames as a host would, requesting the capture on CaptureFrame when a path is
	// given. The scripted click is queued first on its frame, before the shell's Tick and the demo's PreFrame consume
	// that frame's pointers — through both, as a real pointer reaches both. Then the order both hosts use (the
	// sandbox's SandboxApp and the browser's App): the shell ticks and composites, and only then does the demo update
	// and run its PreFrame, so anything a demo starts there (a storyboard on the shell's clock) is as many ticks old at
	// the captured frame here as in a browser capture.
	private static void RunFrames(IEngine engine, IDemo demo, DemoScene scene, UIHost uiHost, BinaryVibrance.Kaldra.Concepts.Camera camera,
		int frames, string? capturePath, bool withUI)
	{
		var surfaceSize = new Vector2(Width, Height);
		var inputs = new FrameInputs { Time = CaptureTime, DeltaSeconds = CaptureDelta };
		for (var frame = 1; frame <= frames; frame++)
		{
			if (withUI && frame == CaptureClickFrame && demo.CaptureClick(camera, surfaceSize) is { } click)
			{
				uiHost.QueuePointer(PointerInput.Press(click));
				uiHost.QueuePointer(PointerInput.Release(click));
				demo.OnPointer(PointerInput.Press(click));
				demo.OnPointer(PointerInput.Release(click));
			}
			uiHost.Tick(CaptureDelta, CaptureTime, surfaceSize);
			uiHost.Render();
			scene.UI.Prepare();
			demo.Update(camera);
			if (withUI)
				demo.PreFrame(CaptureDelta, CaptureTime, camera, surfaceSize);
			if (frame == CaptureFrame && capturePath is not null)
				engine.RequestCapture(capturePath);
			engine.Render(inputs);
		}
	}
}
