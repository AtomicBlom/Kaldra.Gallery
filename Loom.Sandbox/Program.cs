using System.Numerics;

#if LOOM_IMGUI
using ImGuiNET;
#endif
using Loom.Assets;
using Loom.Core;
using Loom.Sandbox.Demos;
using Loom.Vulkan; // for VulkanEngineSettings only — the one sanctioned backend type (Planning.md §5)
using Silk.NET.Input;
using Silk.NET.Maths;
using Silk.NET.Windowing;

namespace Loom.Sandbox;

// The launcher. It owns the window, the engine, the shared geometry, and the per-frame loop — but knows nothing
// about any one effect. Each capability lives in its own IDemo class under Demos/; this picks one, builds its
// graph, and swaps the active graph at runtime (keys 1-N / Tab). Read a demo file to see how that feature works.
internal static class Program
{
	private static void Main()
	{
#if LOOM_DX12
		// The DX12 backend's first slice runs through its own focused harness (the demo gallery below is
		// graph-driven + Vulkan-surface-specific). Opt in with LOOM_BACKEND=DX12 on a UseDX12=true build.
		if (string.Equals(Environment.GetEnvironmentVariable("LOOM_BACKEND"), "DX12", StringComparison.OrdinalIgnoreCase))
		{
			Dx12Smoke.Run();
			return;
		}
#endif

		// Frame-pacing knobs. V-Sync ON by default; LOOM_VSYNC=0/false/off/no flips to a low-latency present
		// mode, and LOOM_MAXFPS bounds the uncapped loop. LOOM_HDR=1 requests true HDR display output.
		var vsyncEnv = Environment.GetEnvironmentVariable("LOOM_VSYNC");
		var vsync = vsyncEnv is null || vsyncEnv.ToLowerInvariant() is not ("0" or "false" or "off" or "no");
		var maxFps = double.TryParse(Environment.GetEnvironmentVariable("LOOM_MAXFPS"), out var f) && f > 0 ? f : 0.0;
		var hdrEnv = Environment.GetEnvironmentVariable("LOOM_HDR");
		var hdr = hdrEnv is not null && hdrEnv.ToLowerInvariant() is "1" or "true" or "on" or "yes";

		var options = WindowOptions.DefaultVulkan with
		{
			Size = new Vector2D<int>(1280, 720),
			Title = "Loom - M7: per-capability demo gallery",
			FramesPerSecond = maxFps,
		};

		using var window = Window.Create(options);
		window.Initialize();

		if (window.VkSurface is null)
			throw new InvalidOperationException("The windowing platform did not expose a Vulkan surface.");

		// Engine config is a settings object (Planning.md §3). VulkanEngineSettings is the one backend type the
		// app names; constructing it loads Loom.Vulkan, which self-registers the Vulkan backend.
		var settings = new VulkanEngineSettings
		{
			EnableValidation = true,
			VSync = vsync,
			MaxFramesPerSecond = maxFps,
			HdrOutput = hdr,
			PreferredBackend = Environment.GetEnvironmentVariable("LOOM_BACKEND"),
			// M13 shader hot-reload (dev opt-in): watch the loose shader dir for rebuilt .spv/.json artifacts.
			WatchShaderDirectory = Environment.GetEnvironmentVariable("LOOM_WATCH_SHADERS") is "1" or "true",
		};

		// Pick a backend at runtime by name, with a sensible fallback — the app names no graphics-API type here.
		var backend = Engine.AvailableEngines.FirstOrDefault(e => e.Name == settings.PreferredBackend)
			?? Engine.AvailableEngines.First();
		using var engine = backend.Create(window.VkSurface, window.FramebufferSize.X, window.FramebufferSize.Y, settings);

		// Shared geometry every demo draws, uploaded once.
		var (cubeVerts, cubeIndices) = Geometry.BuildCube();
		var (planeVerts, planeIndices) = Geometry.BuildPlane();
		// The shared brick material — albedo + tangent-space normal map, decoded from committed PNGs (M11). The
		// loaders live in Loom.Assets (neutral); the engine just uploads the resulting TextureData + mip chain.
		var assets = Path.Combine(AppContext.BaseDirectory, "assets");
		var brick = engine.CreateMaterial(
			engine.CreateTexture(ImageLoader.Load(Path.Combine(assets, "brick_albedo.png"), TextureFormat.Rgba8Srgb)),
			engine.CreateTexture(ImageLoader.Load(Path.Combine(assets, "brick_normal.png"), TextureFormat.Rgba8Unorm)));
		var scene = new DemoScene(engine.CreateMesh(cubeVerts, cubeIndices), engine.CreateMesh(planeVerts, planeIndices), brick);

		// The demo gallery — one focused, legible graph per capability, plus the composed reference frame.
		IDemo[] demos =
		[
			new CubeLightingDemo(),
			new ShadowsDemo(),
			new SsaoDemo(),
			new TonemappingDemo(),
			new BloomDemo(),
			new ParticlesDemo(),
			new HighlightingDemo(),
			new InstancingDemo(),
			new FullReferenceDemo(),
			new NormalMappingDemo(),
			new GltfDemo(),
		];

		// Register every demo's techniques once (idempotent across demos that share one), then build each graph.
		foreach (var demo in demos)
			demo.DefineTechniques(engine);
		var graphs = demos.Select(d => d.Build(scene)).ToArray();

		// Start on the first (simplest) demo; LOOM_DEMO=<name|index> overrides.
		var currentDemo = 0;
		var demoEnv = Environment.GetEnvironmentVariable("LOOM_DEMO");
		if (demoEnv is not null)
		{
			var byName = Array.FindIndex(demos, d => string.Equals(d.Name, demoEnv, StringComparison.OrdinalIgnoreCase));
			if (byName >= 0)
				currentDemo = byName;
			else if (int.TryParse(demoEnv, out var byIndex) && byIndex >= 0 && byIndex < demos.Length)
				currentDemo = byIndex;
			else
				Console.WriteLine($"[loom] LOOM_DEMO='{demoEnv}' is not a known demo ({string.Join(", ", demos.Select(d => d.Name))}); starting on {demos[currentDemo].Name}.");
		}

		// M13 graph-viz export. LOOM_DUMP_GRAPH=<prefix> writes "<prefix>.<demo>.dot" (data-flow) + ".json" for the
		// active graph on startup and every swap; the ImGui "Export graph" menu item writes the same on demand.
		var dumpGraphPrefix = Environment.GetEnvironmentVariable("LOOM_DUMP_GRAPH");

		void DumpGraph(int index, string prefix)
		{
			var safe = string.Concat(demos[index].Name.Split(Path.GetInvalidFileNameChars())).Replace(' ', '_');
			var dot = $"{prefix}.{safe}.dot";
			var json = $"{prefix}.{safe}.json";
			File.WriteAllText(dot, graphs[index].ToDot());
			File.WriteAllText(json, graphs[index].ToJson());
			Console.WriteLine($"[loom] graph → {dot} + {json}");
		}

#if LOOM_IMGUI
		// Dear ImGui (§6/§9 HUD). The host owns the context + input; the engine's renderer does the GPU work.
		// Compiled out entirely when the engine is built without ImGui (UseImGui=false). Enabled BEFORE the first
		// SetGraph so the engine injects the M13 resource-viewer sample edges into the initial graph too.
		ImGui.CreateContext();
		var io = ImGui.GetIO();
		unsafe { io.NativePtr->IniFilename = null; } // don't persist a layout .ini next to the exe
		io.DisplaySize = new Vector2(window.FramebufferSize.X, window.FramebufferSize.Y);
		io.DisplayFramebufferScale = Vector2.One;
		io.Fonts.AddFontDefault();
		ImGui.StyleColorsDark();
		engine.EnableImGui();
		var showDemoWindow = false;
		var showInspect = Environment.GetEnvironmentVariable("LOOM_INSPECT") is "1" or "true"; // auto-open for headless capture verification
#endif

		// MU0 Phase A: stand up the game-UI seam (no-op renderer) so the demos' "ui" pass + ctx.DrawUI() run clean
		// on both backends. Independent of ImGui (not gated). The real UiHost/compositor arrive in MU0 Phase B; for
		// now the host hands one empty overlay surface each frame — DrawUI finds it by id, sees it empty, and skips.
		engine.EnableUI();
		var uiSurfaces = new (UiSurfaceId Id, UIDrawData Draw)[] { (UiSurface.Hud, UIDrawData.Empty) };

		engine.SetGraph(graphs[currentDemo]);
		if (dumpGraphPrefix is not null)
			DumpGraph(currentDemo, dumpGraphPrefix);

		void SelectDemo(int index)
		{
			if (index < 0 || index >= demos.Length || index == currentDemo)
				return;
			currentDemo = index;
			engine.SetGraph(graphs[index]); // drains the GPU, releases the old transients, binds the new graph
			Console.WriteLine($"[loom] demo → {demos[index].Name}");
			if (dumpGraphPrefix is not null)
				DumpGraph(index, dumpGraphPrefix);
		}

		// The sun: a directional light, constant — built once and handed in every frame's inputs. Neutral
		// projection (+Y up); the backend applies the Vulkan clip-space Y-flip on ingest (M7-B5).
		var lightDir = Vector3.Normalize(new Vector3(0.45f, 0.55f, -0.6f));
		var sunView = Matrix4x4.CreateLookAt(lightDir * 7f, Vector3.Zero, Vector3.UnitY);
		var sunProj = Matrix4x4.CreateOrthographic(9f, 9f, 0.1f, 14f);
		var sunCamera = new Camera(sunView, sunProj);

		// A real HUD control (freezes the orbit); also togglable via the ImGui Window menu when built with ImGui.
		// Declared unconditionally because the camera loop reads it whether or not ImGui is compiled in.
		var pauseCamera = false;

		// M13 detached debug camera + frustum freeze. F (or the Window menu) toggles a free-fly camera; entering it
		// freezes the scripted camera's frustum, which the engine draws as a yellow screen-space wireframe so you can
		// fly around and see exactly what that camera framed. LOOM_FRUSTUM=1 enables it for a deterministic capture.
		var fly = new FlyCamera();
		var debugCam = Environment.GetEnvironmentVariable("LOOM_FRUSTUM") is "1" or "true";
		var debugCamInit = false;
		var frozenViewProjection = Matrix4x4.Identity;
		var frustumSegments = new Vector4[12];

		Console.WriteLine($"Loom M7 - demo gallery on {engine.DeviceName} ({engine.BackendName}). Keys 1-{demos.Length} / Tab switch demos (the engine swaps the active graph live); on Object Highlighting / Full, hover the cube. Close the window to exit.");

		using var input = window.CreateInput();
		var mouse = input.Mice.Count > 0 ? input.Mice[0] : null;
		var keyboard = input.Keyboards.Count > 0 ? input.Keyboards[0] : null;
		if (keyboard is not null)
			keyboard.KeyDown += (_, key, _) =>
			{
				if (key == Key.Tab)
					SelectDemo((currentDemo + 1) % demos.Length);
				else if (key >= Key.Number1 && key < Key.Number1 + demos.Length)
					SelectDemo(key - Key.Number1);
			};

		if (keyboard is not null)
			keyboard.KeyDown += (_, key, _) =>
			{
				if (key == Key.F)
					debugCam = !debugCam; // M13: toggle the detached debug camera + frustum freeze
			};

		// Headless smoke (LOOM_SMOKE_FRAMES=N) + capture (LOOM_CAPTURE=path) modes.
		var smokeFrames = int.TryParse(Environment.GetEnvironmentVariable("LOOM_SMOKE_FRAMES"), out var n) ? n : 0;
		var capturePath = Environment.GetEnvironmentVariable("LOOM_CAPTURE");

		// M13 GPU profiler: in a headless smoke run, turn timing on so the final frame can dump per-pass numbers for
		// a cross-backend sanity check (no-op if the queue can't timestamp; the setter guards it).
		if (smokeFrames > 0)
			engine.GpuTimingEnabled = true;
		var frame = 0;
		var elapsed = 0f;
		var fpsTimer = 0f;
		var fpsFrames = 0;
		var displayFps = 0.0;
		var displayMs = 0.0;

		window.FramebufferResize += size => engine.Resize(size.X, size.Y);
		window.Render += delta =>
		{
			frame++;

			// Smoke mode also validates the runtime graph-swap: render each demo in turn (2 frames each) so a
			// headless run exercises a SetGraph into every pipeline. Skipped under capture (which wants a steady
			// frame of the selected demo).
			if (smokeFrames > 0 && capturePath is null)
				SelectDemo(Math.Min((frame - 1) / 2, demos.Length - 1));

			if (!pauseCamera)
				elapsed += (float)delta;

			fpsTimer += (float)delta;
			fpsFrames++;
			if (fpsTimer >= 0.25f)
			{
				displayFps = fpsFrames / fpsTimer;
				displayMs = fpsTimer / fpsFrames * 1000.0;
				fpsTimer = 0f;
				fpsFrames = 0;
			}

			// Orbit the camera around the cube. Neutral projection — the backend applies the Vulkan Y-flip.
			// When capturing, FREEZE the orbit at the same fixed angle the DX12 harness uses, so the two backends'
			// captures are the identical viewpoint (a deterministic A/B for pixel-diffing). The scene time and frame
			// delta are frozen too (below), so the particle phase and the delta-driven auto-exposure converge
			// identically on both backends — captured at the same frame, the dynamic demos diff like the static ones.
			const float captureTime = 1.0f;
			const double captureDelta = 1.0 / 60.0;
			var camTime = capturePath is not null ? 0.6f : elapsed;
			var eye = new Vector3(MathF.Sin(camTime) * 2.5f, 1.6f, MathF.Cos(camTime) * 2.5f);
			var view = Matrix4x4.CreateLookAt(eye, Vector3.Zero, Vector3.UnitY);
			var aspect = window.FramebufferSize.Y == 0 ? 1f : (float)window.FramebufferSize.X / window.FramebufferSize.Y;
			var projection = Matrix4x4.CreatePerspectiveFieldOfView(MathF.PI / 3f, aspect, 0.1f, 100f);
			var camera = new Camera(view, projection);

			// Per-frame CPU work for the active demo (e.g. the instancing demo's frustum cull) — runs with this
			// frame's MAIN camera (the one the debug view freezes), before Render, so the surviving draw list is
			// ready when the pass closure records. In freeze mode the demo still culls against the main frustum
			// while we fly the detached camera around to inspect what it framed.
			demos[currentDemo].Update(camera);

			// M13 detached debug camera: when active, render from a free-fly view and draw the FROZEN scripted-camera
			// frustum as a yellow wireframe. Entering debug mode freezes the current view; the host projects the frozen
			// frustum's 8 corners to screen pixels and hands the 12 edges to the engine's overlay.
			var renderCamera = camera;
			if (debugCam)
			{
				if (!debugCamInit)
				{
					debugCamInit = true;
					frozenViewProjection = camera.ViewProjection;
					fly.PlaceLookingAt(new Vector3(6f, 4f, 6f), Vector3.Zero); // start outside the frozen frustum
				}
				// A fixed detached viewpoint under capture (deterministic); free-fly otherwise.
				renderCamera = capturePath is not null
					? new Camera(Matrix4x4.CreateLookAt(new Vector3(6f, 4f, 6f), Vector3.Zero, Vector3.UnitY), projection)
					: fly.Update(keyboard, mouse, (float)delta, aspect);

				var corners = frozenViewProjection.GetFrustumCorners();
				var vp = renderCamera.ViewProjection;
				var fbW = window.FramebufferSize.X;
				var fbH = window.FramebufferSize.Y;
				var segmentCount = 0;
				var edges = Frustum.EdgeIndices;
				for (var e = 0; e < edges.Length; e += 2)
					if (ProjectToScreen(corners[edges[e]], vp, fbW, fbH, out var p0) &&
						ProjectToScreen(corners[edges[e + 1]], vp, fbW, fbH, out var p1))
						frustumSegments[segmentCount++] = new Vector4(p0.X, p0.Y, p1.X, p1.Y);
				engine.SetDebugLines(frustumSegments.AsSpan(0, segmentCount));
			}
			else
			{
				debugCamInit = false;
				engine.SetDebugLines(ReadOnlySpan<Vector4>.Empty);
			}

			// Hover (for Object Highlighting / Full): map the cursor into the 320×180 picking domain and read
			// the object id back (§7.4). In capture, force the cube hovered so the outline shows. Demos without
			// a readback return 0 from SamplePick, so this is harmless for them.
			uint hoverId;
			if (capturePath is not null)
			{
				hoverId = 1u;
			}
			else if (mouse is not null)
			{
				var px = (int)(mouse.Position.X / Math.Max(window.FramebufferSize.X, 1) * 320);
				var py = (int)(mouse.Position.Y / Math.Max(window.FramebufferSize.Y, 1) * 180);
				hoverId = (uint)MathF.Round(engine.SamplePick(px, py));
			}
			else
			{
				hoverId = 0u;
			}

			if (capturePath is not null && frame == 60)
				engine.RequestCapture(capturePath); // late enough that the delta-driven auto-exposure has converged

			// ImDrawData* for this frame; stays null (no UI) when built without ImGui — the hud pass's
			// ctx.DrawImGui() is then a no-op, so every demo still renders, just without the overlay.
			nint imguiDrawData = 0;
#if LOOM_IMGUI
			// Drive ImGui: feed input, build the bar, produce the draw data this frame's FrameInputs forwards.
			io.DisplaySize = new Vector2(window.FramebufferSize.X, window.FramebufferSize.Y);
			io.DisplayFramebufferScale = Vector2.One;
			io.DeltaTime = delta > 0 ? (float)delta : 1f / 60f;
			if (mouse is not null)
			{
				io.AddMousePosEvent(mouse.Position.X, mouse.Position.Y);
				io.AddMouseButtonEvent(0, mouse.IsButtonPressed(MouseButton.Left));
				io.AddMouseButtonEvent(1, mouse.IsButtonPressed(MouseButton.Right));
				io.AddMouseWheelEvent(0f, mouse.ScrollWheels.Count > 0 ? mouse.ScrollWheels[0].Y : 0f);
			}

			ImGui.NewFrame();

			// Title bar: the Demo menu switches the active graph (same as keys 1..N / Tab); Window holds host
			// toggles; the status rides the bar (a bottom footer needs SetNextWindowPos, HFA-broken on win-arm64).
			if (ImGui.BeginMainMenuBar())
			{
				if (ImGui.BeginMenu("Demo"))
				{
					for (var i = 0; i < demos.Length; i++)
						if (ImGui.MenuItem($"{i + 1}. {demos[i].Name}", "", currentDemo == i))
							SelectDemo(i);
					ImGui.EndMenu();
				}
				if (ImGui.BeginMenu("Window"))
				{
					ImGui.MenuItem("Pause camera", "", ref pauseCamera);
					ImGui.MenuItem("Debug camera (fly) + frustum freeze", "F", ref debugCam);
					ImGui.MenuItem("ImGui demo window", "", ref showDemoWindow);
					var gpuTiming = engine.GpuTimingEnabled;
					if (ImGui.MenuItem("GPU timing", "", ref gpuTiming))
						engine.GpuTimingEnabled = gpuTiming;
					ImGui.MenuItem("Resource viewer", "", ref showInspect);
					if (ImGui.MenuItem("Export graph (DOT/JSON)"))
						DumpGraph(currentDemo, dumpGraphPrefix ?? "loom-graph");
					ImGui.EndMenu();
				}

				ImGui.Separator();
				var status = $"{displayFps:F0} fps ({displayMs:F2} ms)   |   demo: {demos[currentDemo].Name}   |   hovered {hoverId}   |   {engine.DeviceName}";
				if (demos[currentDemo].HudStatus is { } demoStatus)
					status += $"   |   {demoStatus}";
				ImGui.Text(status);

				ImGui.EndMainMenuBar();
			}

			if (showDemoWindow)
				ImGui.ShowDemoWindow(ref showDemoWindow);

			// M13 resource viewer: enumerate the active graph's colour/depth resources and preview each via
			// ImGui.Image (the engine binds the resource's live, ShaderRead view to the returned id). Click a
			// thumbnail to enlarge it. Works for any loaded graph — the buffer list comes from the engine.
			if (showInspect)
			{
				if (ImGui.Begin("Resource viewer", ref showInspect))
				{
					// This window is the legend; the engine draws the matching thumbnail grid (row-major, same order)
					// to the right. GetDebugTextureId returns non-zero for inspectable resources AND requests the
					// thumbnail for this frame. (ImGui.Image can't draw the textures itself on win-arm64.)
					ImGui.Text("Buffers (grid is row-major, same order):");
					var slot = 0;
					foreach (var r in engine.InspectableResources)
						if (engine.GetDebugTextureId(r.Id) != 0)
							ImGui.Text($"{slot++,2}  {r.Name}  ({r.Kind} {r.Format})");
				}
				ImGui.End();
			}

			// M13 GPU profiler HUD: per-pass GPU ms + frame total from the most recently completed frame.
			if (engine.GpuTimingEnabled)
			{
				if (ImGui.Begin("GPU Timing"))
				{
					foreach (var t in engine.LastFrameTimings)
						ImGui.Text($"{t.Name,-18} {t.GpuMilliseconds,7:F3} ms");
					ImGui.Separator();
					ImGui.Text($"{"frame total",-18} {engine.LastFrameGpuMilliseconds,7:F3} ms");
				}
				ImGui.End();
			}

			ImGui.Render();
			unsafe { imguiDrawData = (nint)ImGui.GetDrawData().NativePtr; }
#endif

			engine.Render(new FrameInputs
			{
				DeltaSeconds = capturePath is not null ? captureDelta : delta,
				Time = capturePath is not null ? captureTime : elapsed,
				Camera = renderCamera,
				SunView = sunCamera,
				HoverId = hoverId,
				ImGuiDrawData = imguiDrawData,
				UiSurfaces = uiSurfaces,
			});

			if (capturePath is not null && frame >= 62)
				window.Close();
			else if (smokeFrames > 0 && frame >= smokeFrames)
			{
				// M13: dump the last completed frame's GPU timings (cross-backend sanity: sum ≤ frame total ≤ wall).
				if (engine.GpuTimingEnabled && engine.LastFrameTimings.Count > 0)
				{
					Console.WriteLine($"[loom] GPU timing ({engine.BackendName}, demo {demos[currentDemo].Name}):");
					foreach (var t in engine.LastFrameTimings)
						Console.WriteLine($"  {t.Name,-18} {t.GpuMilliseconds,7:F3} ms");
					Console.WriteLine($"  {"frame total",-18} {engine.LastFrameGpuMilliseconds,7:F3} ms");
				}
				window.Close();
			}
		};

		window.Run();
		engine.WaitIdle();
#if LOOM_IMGUI
		ImGui.DestroyContext();
#endif
	}

	// Project a world-space point to framebuffer pixels (top-left origin) through a neutral view-projection (M13
	// frustum wireframe). Returns false for points behind the camera (w ≤ 0), so edges that cross the near plane are
	// dropped rather than drawn wrapped. The engine's overlay maps these pixels to clip space on either backend.
	private static bool ProjectToScreen(Vector3 world, Matrix4x4 viewProjection, int width, int height, out Vector2 pixel)
	{
		var clip = Vector4.Transform(new Vector4(world, 1f), viewProjection);
		if (clip.W <= 1e-4f)
		{
			pixel = default;
			return false;
		}
		var ndc = new Vector2(clip.X, clip.Y) / clip.W;
		pixel = new Vector2((ndc.X * 0.5f + 0.5f) * width, (0.5f - ndc.Y * 0.5f) * height);
		return true;
	}
}
