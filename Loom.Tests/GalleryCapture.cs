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
	/// </summary>
	public static bool TryRender(IRenderBackend backend, Func<IDemo> demoFactory, string outPngPath)
	{
		using var engine = backend.Create(Surface.Offscreen(Width, Height), Width, Height, new EngineSettings());

		DemoGraph.Overlay = null; // chrome-free: no Dear ImGui overlay in the hud pass

		var demo = demoFactory();
		try
		{
			demo.DefineTechniques(engine);
		}
		catch (NotSupportedException)
		{
			return false; // a stage with no DXIL on this toolchain — nothing to compare, not a failure
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
			if (frame == CaptureFrame)
				engine.RequestCapture(outPngPath);
			engine.Render(inputs);
		}
		return true;
	}

	// The shared scene every demo draws against: cube/plane/quad geometry, the brick albedo+normal material, and a
	// UI painter over an empty host (so the demos' hud pass composites nothing — the chrome-free capture).
	private static DemoScene BuildScene(IEngine engine, UIHost uiHost)
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
