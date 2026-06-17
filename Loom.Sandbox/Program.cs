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

		engine.SetGraph(graphs[currentDemo]);

		void SelectDemo(int index)
		{
			if (index < 0 || index >= demos.Length || index == currentDemo)
				return;
			currentDemo = index;
			engine.SetGraph(graphs[index]); // drains the GPU, releases the old transients, binds the new graph
			Console.WriteLine($"[loom] demo → {demos[index].Name}");
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
#if LOOM_IMGUI
		// Dear ImGui (§6/§9 HUD). The host owns the context + input; the engine's renderer does the GPU work.
		// Compiled out entirely when the engine is built without ImGui (UseImGui=false).
		ImGui.CreateContext();
		var io = ImGui.GetIO();
		unsafe { io.NativePtr->IniFilename = null; } // don't persist a layout .ini next to the exe
		io.DisplaySize = new Vector2(window.FramebufferSize.X, window.FramebufferSize.Y);
		io.DisplayFramebufferScale = Vector2.One;
		io.Fonts.AddFontDefault();
		ImGui.StyleColorsDark();
		engine.EnableImGui();
		var showDemoWindow = false;
#endif

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

		// Headless smoke (LOOM_SMOKE_FRAMES=N) + capture (LOOM_CAPTURE=path) modes.
		var smokeFrames = int.TryParse(Environment.GetEnvironmentVariable("LOOM_SMOKE_FRAMES"), out var n) ? n : 0;
		var capturePath = Environment.GetEnvironmentVariable("LOOM_CAPTURE");
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
			// frame's camera, before Render, so the surviving draw list is ready when the pass closure records.
			demos[currentDemo].Update(camera);

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
					ImGui.MenuItem("ImGui demo window", "", ref showDemoWindow);
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

			ImGui.Render();
			unsafe { imguiDrawData = (nint)ImGui.GetDrawData().NativePtr; }
#endif

			engine.Render(new FrameInputs
			{
				DeltaSeconds = capturePath is not null ? captureDelta : delta,
				Time = capturePath is not null ? captureTime : elapsed,
				Camera = camera,
				SunView = sunCamera,
				HoverId = hoverId,
				ImGuiDrawData = imguiDrawData,
			});

			if (capturePath is not null && frame >= 62)
				window.Close();
			else if (smokeFrames > 0 && frame >= smokeFrames)
				window.Close();
		};

		window.Run();
		engine.WaitIdle();
#if LOOM_IMGUI
		ImGui.DestroyContext();
#endif
	}
}
