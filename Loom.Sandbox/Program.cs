using System.Linq;
using System.Numerics;

using ImGuiNET;
using Loom.Assets;
using Loom.Core;
using Loom.Core.Geometry;
using Loom.Core.Graph;
using Loom.Core.Hosting;
using Loom.Core.Materials;
#if LOOM_DIRECTX12
using Loom.DirectX12; // for DirectX12EngineSettings only — the one sanctioned DirectX12 backend type
#endif
using Loom.Sandbox.Demos;
using Loom.Sandbox.UI;
using Loom.UI.SUIsei;
// Alias the painter type only — importing the Loom.UI.ImGui namespace would shadow ImGuiNET's bare `ImGui`.
using ImGuiPainter = Loom.UI.ImGui.ImGuiPainter;
using SUIsei;
using SUIsei.Controls;
using SUIsei.Drawing;
using SUIsei.Input;
#if LOOM_VULKAN
using Loom.Vulkan; // for VulkanEngineSettings only — the one sanctioned Vulkan backend type
#endif
using Silk.NET.Input;
using Silk.NET.Maths;
using Silk.NET.Windowing;

namespace Loom.Sandbox;

// The launcher. It owns the window, the engine, the shared geometry, and the per-frame loop — but knows nothing
// about any one effect. Each capability lives in its own IDemo class under Demos/; this picks one, builds its
// graph, and swaps the active graph at runtime (keys 1-N / Tab). Read a demo file to see how that feature works.
//
// ONE host serves BOTH backends: --backend vulkan (default) or --backend dx12 swaps the window surface + engine
// settings (CreateWindow/CreateEngine); the demo gallery, the per-frame loop, and every debug feature are shared,
// which is the portability thesis made concrete (and removes the old Program.cs/DirectX12Smoke.cs sync hazard). Every
// knob is a command-line option — run with --help for the list (there are no LOOM_* environment variables).
internal static class Program
{
	private static void Main(string[] args)
	{
		var opts = LaunchOptions.Parse(args);
		if (opts.ShowHelp)
		{
			Console.WriteLine(LaunchOptions.Usage);
			return;
		}

		// Pick a backend from the set actually compiled into this build (--backend chooses; otherwise the first
		// compiled-in backend, Vulkan when present). The window surface and engine settings both follow from it.
		var backend = ChooseBackend(opts.Backend);
		using var window = CreateWindow(backend, opts);
		using var engine = CreateEngine(backend, window, opts);

		// Shared geometry every demo draws, uploaded once.
		var (cubeVerts, cubeIndices) = Geometry.BuildCube();
		var (planeVerts, planeIndices) = Geometry.BuildPlane();
		var (quadVerts, quadIndices) = Geometry.BuildQuad();
		// The shared brick material — albedo + tangent-space normal map, decoded from committed PNGs. The
		// loaders live in Loom.Assets (neutral); the engine just uploads the resulting TextureData + mip chain.
		var assets = Path.Combine(AppContext.BaseDirectory, "assets");
		var brick = engine.CreateMaterial(
			engine.CreateTexture(ImageLoader.Load(Path.Combine(assets, "brick_albedo.png"), TextureFormat.Rgba8Srgb)),
			engine.CreateTexture(ImageLoader.Load(Path.Combine(assets, "brick_normal.png"), TextureFormat.Rgba8Unorm)));
		// The game-UI painter: registers the "ui" technique and uploads the white texel + default glyph atlas through
		// the public API, then renders each composited overlay list. Created up front so the demo graphs capture it.
		var uiHost = new UIHost();
		var uiPainter = UIPainter.Create(engine, uiHost, FontAtlas.Default);
		var scene = new DemoScene(engine.CreateMesh(cubeVerts, cubeIndices), engine.CreateMesh(planeVerts, planeIndices), engine.CreateMesh(quadVerts, quadIndices), brick, uiPainter, name => File.ReadAllBytesAsync(Path.Combine(assets, name)));

		// The demo gallery — one focused, legible graph per capability, plus the composed reference frame. Only
		// registers techniques (cheap, synchronous — this is also the backend-capability filter, see BuildGallery)
		// up front; each demo's actual asset preload + graph build is lazy (see EnsureLoadedAsync below), so opening
		// the sandbox no longer pays every demo's asset cost regardless of whether it's ever viewed.
		var demos = BuildGallery(engine);

		// Start on the first (simplest) demo; --demo overrides (index / exact name / unique substring). An
		// unresolved query (ambiguous or no match) prints why and exits — the window + engine dispose via `using`.
		var currentDemo = ResolveDemoIndex(demos, opts.Demo);
		if (currentDemo < 0)
			return;

		// Per-demo lazy load state: graphs[i] / loadTasks[i] are populated the first time demo i is loaded (the
		// start demo, blocked on below; any other demo, the first time it's selected — see EnsureLoadedAsync).
		// loadTasks caches the in-flight/completed task per index so a repeated request (e.g. mashing Tab) awaits
		// the same load instead of double-loading, and a revisit after the first load resolves instantly.
		var graphs = new RenderGraph?[demos.Length];
		var loadTasks = new Task<RenderGraph>?[demos.Length];
		var pendingDemo = currentDemo;

		Task<RenderGraph> EnsureLoadedAsync(int index) => loadTasks[index] ??= LoadAsync(index);

		async Task<RenderGraph> LoadAsync(int index)
		{
			await demos[index].PreloadAsync(scene);
			var graph = demos[index].Build(scene);
			graphs[index] = graph;
			return graph;
		}

		// Block this (startup) thread until the start demo's assets have loaded — deliberately not `await`ed here:
		// `Main` must stay synchronous so `window`'s creation and `window.Run()` below run on the exact same thread
		// (Win32 windows are thread-affine), while the preload itself still runs concurrently underneath via real
		// background I/O/decode threads. There's no frame loop yet for this to block, so blocking here costs nothing.
		PumpUntilReady(engine, EnsureLoadedAsync(currentDemo)).GetAwaiter().GetResult();

		// The interactive UI demo: when it is active the overlay shows the data-bound page + routes pointer
		// input; otherwise the static panel. -1 if its techniques were dropped on this backend (then never shown).
		var interactiveIndex = Array.FindIndex(demos, d => d.Name == "UI Interactive");
		// The group-opacity demo: when active the overlay shows a faded container of overlapping translucent
		// children, exercising the backend's render-to-texture layer composite.
		var groupOpacityIndex = Array.FindIndex(demos, d => d.Name == "UI Group Opacity");
		// The in-world UI demo: its panel isn't on the shared overlay at all (it's its own InWorldPanel mapped onto
		// a 3D quad), so its deterministic capture click routes through OnPointer only, at a screen position found
		// empirically to land on the billboard panel's Increment button (see the click-injection comment below).
		var inWorldIndex = Array.FindIndex(demos, d => d.Name == "In-World UI");

		// --dump-graph=<prefix> writes "<prefix>.<demo>.dot" (data-flow) + ".json" for the active graph on startup
		// and every swap; the ImGui "Export graph" menu item writes the same on demand.
		var dumpGraphPrefix = opts.DumpGraph;

		void DumpGraph(int index, string prefix)
		{
			// Only ever called with an already-loaded index (the start demo, or a demo just activated).
			var graph = graphs[index] ?? throw new InvalidOperationException($"DumpGraph called on unloaded demo '{demos[index].Name}'.");
			var safe = string.Concat(demos[index].Name.Split(Path.GetInvalidFileNameChars())).Replace(' ', '_');
			var dot = $"{prefix}.{safe}.dot";
			var json = $"{prefix}.{safe}.json";
			File.WriteAllText(dot, graph.ToDot());
			File.WriteAllText(json, graph.ToJson());
			Console.WriteLine($"[loom] graph → {dot} + {json}");
		}

		// Dear ImGui HUD. The host owns the context + input + the per-frame UI; the optional Loom.UI.ImGui
		// bridge renders the draw data through the public draw-list API (no backend ImGui dependency). Inspection is
		// enabled BEFORE the first SetGraph so the resource-viewer sample edges land in the initial graph too.
		ImGui.CreateContext();
		var io = ImGui.GetIO();
		unsafe { io.NativePtr->IniFilename = null; } // don't persist a layout .ini next to the exe
		io.DisplaySize = new Vector2(window.FramebufferSize.X, window.FramebufferSize.Y);
		io.DisplayFramebufferScale = Vector2.One;
		io.Fonts.AddFontDefault();
		ImGui.StyleColorsDark();
		engine.ResourceInspectionEnabled = true;
		var imguiPainter = ImGuiPainter.Create(engine);
		var inspectIds = new List<int>();
		var showDemoWindow = false;
		var showInspect = opts.Inspect; // auto-open for headless capture verification

		// Swap in an already-loaded demo's graph. currentDemo (and therefore every demos[currentDemo] read in the
		// frame loop below) only changes here, once the new demo's graph actually exists, so the previous demo
		// keeps rendering uninterrupted while a switch's load runs in the background.
		void ActivateDemo(int index, RenderGraph graph)
		{
			currentDemo = index;
			engine.SetGraph(graph); // drains the GPU, releases the old transients, binds the new graph
			Console.WriteLine($"[loom] demo → {demos[index].Name}");
			if (dumpGraphPrefix is not null)
				DumpGraph(index, dumpGraphPrefix);
		}

		// Fire-and-forget switch: loads (or reuses the cached load of) the target demo, then activates it — unless
		// a later switch has since superseded this one (pendingDemo no longer matches), in which case this load's
		// result is simply discarded. The per-frame engine.Render call already drains ServicePendingUploads once
		// the frame loop below is running, so no explicit pump is needed here (only the very first, pre-loop load
		// above needs one).
		async Task SwitchDemoAsync(int index)
		{
			pendingDemo = index;
			var graph = await EnsureLoadedAsync(index);
			if (pendingDemo == index)
				ActivateDemo(index, graph);
		}

		void SelectDemo(int index)
		{
			if (index < 0 || index >= demos.Length || index == currentDemo || index == pendingDemo)
				return;
			_ = SwitchDemoAsync(index);
		}

		// A real HUD control (freezes the orbit); also togglable from the shell's debug panel.
		var pauseCamera = false;

		// Detached debug camera + frustum freeze. F (or the debug-panel toggle) enters a free-fly camera; entering it
		// freezes the scripted camera's frustum, which the engine draws as a yellow screen-space wireframe so you can
		// fly around and see exactly what that camera framed. --frustum enables it for a deterministic capture.
		var fly = new FlyCamera(engine);
		var orbit = new Loom.Concepts.Camera(engine); // the default perspective orbit camera, reconfigured each frame
		var debugCam = opts.Frustum;

		// The shared SUIsei demo shell — the switcher, debug panel, and status bar every demo's `hud` pass composites
		// (DemoGraph.Hud). It renders identically on desktop and in the browser; ImGui (below) narrows to the desktop
		// dev tools the shell's checkboxes/buttons open (the demo window, the resource viewer, graph export).
		var shell = new DemoShell([.. demos.Select(d => d.Name)], SelectDemo);
		shell.AddHostToggle("Pause camera", () => pauseCamera, v => pauseCamera = v);
		shell.AddHostToggle("Debug camera (fly) + frustum freeze [F]", () => debugCam, v => debugCam = v);
		shell.AddHostToggle("GPU timing", () => engine.GpuTimingEnabled, v => engine.GpuTimingEnabled = v);
		shell.AddHostToggle("ImGui demo window", () => showDemoWindow, v => showDemoWindow = v);
		shell.AddHostToggle("Resource viewer", () => showInspect, v => showInspect = v);
		shell.AddHostAction("Export graph (DOT/JSON)", () => DumpGraph(currentDemo, dumpGraphPrefix ?? "loom-graph"));
		// --no-hud gates both the shell chrome and the ImGui dev-tools pass — a chrome-free capture the WebGPU backend
		// (which has no Dear ImGui bridge and, for non-UI demos, no centred content) matches byte-for-byte.
		shell.SetHudVisible(opts.Hud);
		DemoGraph.Overlay = opts.Hud ? imguiPainter : null;

		// A screen-space overlay surface driven by the UIHost created above; its compositor lays glyphs out against the
		// same default font the painter uploaded, so UVs match. The root's content is the shell; the UI-track demos'
		// own page/panel is set into the shell's centred slot below, per active demo.
		var overlay = uiHost.CreateRoot(UILayer.Default);
		overlay.Root.Content = shell.Root;

		// The interactive page: XAML → typed tree + {x:Bind} label + Click handler (UI/UIInteractiveView.*).
		var interactivePage = new UIInteractiveView();

		// The group-opacity content: a 50%-opacity container over a Canvas of two overlapping opaque fills. True
		// group opacity composites the subtree once and fades the result, so the overlap stays the same colour as the
		// non-overlap; folding the fade per child would darken the overlap. The backend renders this via its
		// offscreen layer composite — the only UI path whose pixels depend on the backend, hence the parity gate.
		var overlapCanvas = new Canvas { Width = 240f, Height = 180f };
		var fillA = new Border { Width = 150f, Height = 120f, CornerRadius = 8f, Background = HdrColor.FromRgb(80, 170, 255) };
		var fillB = new Border { Width = 150f, Height = 120f, CornerRadius = 8f, Background = HdrColor.FromRgb(80, 170, 255) };
		Canvas.SetLeft(fillA, 0f);
		Canvas.SetTop(fillA, 0f);
		Canvas.SetLeft(fillB, 80f);
		Canvas.SetTop(fillB, 50f);
		overlapCanvas.Children.Add(fillA);
		overlapCanvas.Children.Add(fillB);
		UIElement groupOpacityPanel = new Border
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

		engine.SetGraph(graphs[currentDemo]!);
		if (dumpGraphPrefix is not null)
			DumpGraph(currentDemo, dumpGraphPrefix);
		var debugCamInit = false;
		var frozenViewProjection = Matrix4x4.Identity;
		var debugSegments = new List<Vector4>(); // frustum wireframe + demo debug lines (skeleton)

		Console.WriteLine($"Loom - demo gallery (keys 1-{demos.Length} / Tab) on {engine.DeviceName} ({engine.BackendName}). On Object Highlighting / Full, hover the cube. Close the window to exit.");

		using var input = window.CreateInput();
		var mouse = input.Mice.Count > 0 ? input.Mice[0] : null;
		var keyboard = input.Keyboards.Count > 0 ? input.Keyboards[0] : null;
		if (keyboard is not null)
		{
			keyboard.KeyDown += (kb, key, _) =>
			{
				// When the interactive UI demo is active, route keys to the UI host first (for focus + keyboard
				// activation). Tab is consumed by the UI for focus navigation and must not also switch demos.
				if (currentDemo == interactiveIndex)
				{
					var vk = ToVirtualKey(key);
					if (vk != VirtualKey.None)
						uiHost.QueueKey(new KeyInput(vk, Down: true, Repeat: false, GetModifiers(kb), 0));
					if (key == Key.Tab)
						return;
				}
				if (key == Key.Tab)
					SelectDemo((currentDemo + 1) % demos.Length);
				else if (key >= Key.Number1 && key < Key.Number1 + demos.Length)
					SelectDemo(key - Key.Number1);
				else if (key == Key.F)
					debugCam = !debugCam;
			};
			keyboard.KeyUp += (kb, key, _) =>
			{
				if (currentDemo != interactiveIndex)
					return;
				var vk = ToVirtualKey(key);
				if (vk != VirtualKey.None)
					uiHost.QueueKey(new KeyInput(vk, Down: false, Repeat: false, GetModifiers(kb), 0));
			};
			keyboard.KeyChar += (_, ch) =>
			{
				if (currentDemo == interactiveIndex)
					uiHost.QueueText(ch.ToString());
			};
		}

		// Translate raw mouse into neutral pointer events the UIHost routes (the host owns hit-test + routing).
		// Independent of the ImGui/camera/pick mouse uses below — multiple handlers per Silk event are fine.
		// Events are also forwarded to the active demo so it can queue them for in-world projection in PreFrame.
		if (mouse is not null)
		{
			mouse.MouseMove += (_, pos) => { var e = PointerInput.Move(pos); uiHost.QueuePointer(e); demos[currentDemo].OnPointer(e); };
			mouse.MouseDown += (m, btn) => { if (btn == MouseButton.Left) { var e = PointerInput.Press(m.Position); uiHost.QueuePointer(e); demos[currentDemo].OnPointer(e); } };
			mouse.MouseUp   += (m, btn) => { if (btn == MouseButton.Left) { var e = PointerInput.Release(m.Position); uiHost.QueuePointer(e); demos[currentDemo].OnPointer(e); } };
		}

		// Headless smoke (--smoke-frames N) + capture (--capture path) modes.
		var smokeFrames = opts.SmokeFrames;
		var capturePath = opts.Capture;
		// --capture-animate: capture with time ADVANCING deterministically (time = frame·delta) instead of frozen
		// at 1.0. The captured frame (60) lands on the same value, so a correct backend's capture is byte-identical —
		// but the 59 frames before it now differ, so a cross-frame hazard (e.g. an aliased transient reading the
		// previous frame's contents) surfaces, which a frozen capture (identical frames) can't reveal.
		var captureAnimate = opts.CaptureAnimate;

		// GPU profiler: in a headless smoke run, turn timing on so the final frame can dump per-pass numbers for
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
			// frame of the selected demo). Unlike the interactive SelectDemo path, this blocks on each demo's load
			// (rather than firing-and-forgetting it) so a heavy demo is guaranteed to actually be exercised within
			// its 2-frame budget instead of possibly being skipped by a later switch before it finishes loading.
			if (smokeFrames > 0 && capturePath is null)
			{
				var target = Math.Min((frame - 1) / 2, demos.Length - 1);
				if (target != currentDemo)
				{
					pendingDemo = target;
					var task = EnsureLoadedAsync(target);
					while (!task.IsCompleted)
						engine.ServicePendingUploads();
					ActivateDemo(target, task.GetAwaiter().GetResult());
				}
			}

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
			// When capturing, FREEZE the orbit at the same fixed angle on both backends, so the two captures are the
			// identical viewpoint (a deterministic A/B for pixel-diffing). The scene time and frame delta are frozen
			// too (below), so the particle phase and the delta-driven auto-exposure converge identically on both
			// backends — captured at the same frame, the dynamic demos diff like the static ones.
			const float captureTime = 1.0f;
			const double captureDelta = 1.0 / 60.0;
			var camTime = capturePath is not null ? (captureAnimate ? frame * (float)captureDelta : captureTime) : elapsed;
			var aspect = window.FramebufferSize.Y == 0 ? 1f : (float)window.FramebufferSize.X / window.FramebufferSize.Y;
			DemoCamera.Orbit(orbit, camTime, aspect);

			// A demo may override the camera — a 2D/sprite demo returns an orthographic view here; otherwise
			// the perspective orbit above stands. Pulled each frame so a 2D camera can scroll (parallax).
			var camera = demos[currentDemo].GetCamera(aspect, camTime) ?? orbit;

			// Detached debug camera: when active, render from a free-fly view and draw the FROZEN scripted-camera
			// frustum as a yellow wireframe. Entering debug mode freezes the current view; the host projects the frozen
			// frustum's 8 corners to screen pixels and hands the 12 edges to the engine's overlay.
			var renderCamera = camera;
			var fbW = window.FramebufferSize.X;
			var fbH = window.FramebufferSize.Y;
			debugSegments.Clear();
			if (debugCam)
			{
				if (!debugCamInit)
				{
					debugCamInit = true;
					frozenViewProjection = camera.View * camera.Projection; // uncorrected world-space VP for the frustum corners
					fly.PlaceLookingAt(new Vector3(6f, 4f, 6f), Vector3.Zero); // start outside the frozen frustum
				}
				// A fixed detached viewpoint under capture (deterministic); free-fly otherwise.
				renderCamera = capturePath is not null
					? new Loom.Concepts.Camera(engine).LookAt(new Vector3(6f, 4f, 6f), Vector3.Zero).Perspective(MathF.PI / 3f, aspect, 0.1f, 100f)
					: fly.Update(keyboard, mouse, (float)delta, aspect);

				var corners = frozenViewProjection.GetFrustumCorners();
				var vp = renderCamera.View * renderCamera.Projection;
				var edges = Frustum.EdgeIndices;
				for (var e = 0; e < edges.Length; e += 2)
					if (ProjectToScreen(corners[edges[e]], vp, fbW, fbH, out var p0) &&
						ProjectToScreen(corners[edges[e + 1]], vp, fbW, fbH, out var p1))
						debugSegments.Add(new Vector4(p0.X, p0.Y, p1.X, p1.Y));
			}
			else
			{
				debugCamInit = false;
			}

			// The active demo's world-space debug lines (e.g. the skinning demo's skeleton), projected
			// against the render camera and merged with any frustum wireframe.
			var demoLines = demos[currentDemo].DebugWorldLines(capturePath is not null ? captureTime : elapsed);
			if (demoLines.Count > 0)
			{
				var vp = renderCamera.View * renderCamera.Projection;
				for (var i = 0; i < demoLines.Count; i++)
					if (ProjectToScreen(demoLines[i].A, vp, fbW, fbH, out var a) &&
						ProjectToScreen(demoLines[i].B, vp, fbW, fbH, out var b))
						debugSegments.Add(new Vector4(a.X, a.Y, b.X, b.Y));
			}

			imguiPainter.SetDebugLines(System.Runtime.InteropServices.CollectionsMarshal.AsSpan(debugSegments));

			// Hover (Object Highlighting / Full): read the demo's object-id buffer back at the cursor and hand
			// the hovered id to the demo, which writes it into the outline's parameter buffer (WriteHover). The cursor
			// maps through the pick target's own resolution. In capture, force the cube hovered so the outline shows.
			// A demo with no PickTarget skips this entirely — the engine carries no picking state of its own.
			uint hoverId = 0u;
			if (demos[currentDemo].PickTarget is { } pickTarget)
			{
				if (capturePath is not null)
				{
					hoverId = 1u;
				}
				else if (mouse is not null)
				{
					var (pw, ph) = pickTarget.Resolve();
					var px = (int)(mouse.Position.X / Math.Max(window.FramebufferSize.X, 1) * pw);
					var py = (int)(mouse.Position.Y / Math.Max(window.FramebufferSize.Y, 1) * ph);
					var pixels = System.Runtime.InteropServices.MemoryMarshal.Cast<byte, Half>(engine.Read(pickTarget));
					var i = py * pw + px;
					if ((uint)i < (uint)pixels.Length)
						hoverId = (uint)MathF.Round((float)pixels[i]);
				}
				demos[currentDemo].WriteHover(engine, hoverId);
			}

			if (capturePath is not null && frame == 60)
				engine.RequestCapture(capturePath); // late enough that the delta-driven auto-exposure has converged

			// Drive ImGui: feed input, build the bar; the Loom.UI.ImGui bridge renders the resulting draw data in
			// the hud pass (it reads ImGui.GetDrawData() from the pass closure — no draw-data pointer plumbing).
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

			// Drive the shared SUIsei shell: the switcher lives in its MenuBar (SelectDemo, wired at construction);
			// everything else here just refreshes the bits that change per frame — the active demo's name + debug
			// panel, the engine name, the smoothed fps, and the status line (hovered id + the demo's own HudStatus).
			shell.SetActiveDemo(demos[currentDemo].Name, demos[currentDemo].DebugPanel);
			shell.SetEngineName(engine.DeviceName);
			shell.SetFps((float)displayFps, (float)displayMs);
			shell.SetStatus($"hovered {hoverId}");
			shell.SetStatusRight(demos[currentDemo].HudStatus);

			if (showDemoWindow)
				ImGui.ShowDemoWindow(ref showDemoWindow);

			// Resource viewer: enumerate the active graph's colour/depth resources; this window is the legend and the
			// bridge draws the matching thumbnail grid (row-major, same order) to the right via the public draw-list
			// API (ImGui.Image can't draw the textures itself on win-arm64). The id list comes from the engine.
			inspectIds.Clear();
			if (showInspect)
			{
				if (ImGui.Begin("Resource viewer", ref showInspect))
				{
					ImGui.Text("Buffers (grid is row-major, same order):");
					var slot = 0;
					foreach (var r in engine.InspectableResources)
					{
						inspectIds.Add(r.Id);
						ImGui.Text($"{slot++,2}  {r.Name}  ({r.Kind} {r.Format})");
					}
				}
				ImGui.End();
			}
			imguiPainter.SetThumbnails(System.Runtime.InteropServices.CollectionsMarshal.AsSpan(inspectIds));

			// GPU profiler HUD: per-pass GPU ms + frame total from the most recently completed frame.
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

			// Swap the shell's centred content for the active demo, then drive the UI host: Tick drains queued pointer
			// input → hit-tests → routes (bubble) → synthesizes Click → lays out; Render composites each view →
			// DrawLists. The layout/visual caches + the compositor's frame-level no-op make a static tree near-free.
			// The "hud" pass renders the whole shell (shell chrome + this centred content) via DemoGraph.Hud.
			// The UI demos' page IS the demo subject, so it is never hidden by --no-hud (only the shell chrome is,
			// via shell.SetHudVisible above) — a --no-hud capture of a UI demo is page-only, the chrome-free
			// reference the browser WebGPU backend (no ImGui, and the shell renders identically there) matches.
			shell.SetCenterContent(
				currentDemo == interactiveIndex ? interactivePage
				: currentDemo == groupOpacityIndex ? groupOpacityPanel
				: null);

			// Deterministic capture: with no live mouse, inject one press+release over the page's button (Border at
			// 24,24 + padding 20,16 → button at 44,40, sized 200x56 → centre 144,68) so the captured frame shows the
			// clicked/data-bound state on both backends.
			if (capturePath is not null && currentDemo == interactiveIndex && frame == 30)
			{
				var click = new Vector2(144f, 68f);
				uiHost.QueuePointer(PointerInput.Press(click));
				uiHost.QueuePointer(PointerInput.Release(click));
			}

			// Same idea for the in-world UI demo, but its panel isn't on the shared overlay — route through
			// OnPointer only, matching the real mouse-click handlers below. The screen position was found
			// empirically (by iterating against InWorldPanel.LastHitPixel) to land on the billboard panel's
			// Increment button at the frozen --capture camera pose.
			if (capturePath is not null && currentDemo == inWorldIndex && frame == 30)
			{
				var click = new Vector2(505f, 166f);
				demos[currentDemo].OnPointer(PointerInput.Press(click));
				demos[currentDemo].OnPointer(PointerInput.Release(click));
			}

			uiHost.Tick(delta, elapsed, new Vector2(window.FramebufferSize.X, window.FramebufferSize.Y));
			uiHost.Render();

			// Per-frame CPU work for the active demo — runs here, after any mid-frame demo switch via the ImGui
			// menu, so the current demo's Update always fires before its graph records (prevents null closures).
			// In freeze mode the demo still culls against the main frustum while we fly the detached camera.
			demos[currentDemo].Update(camera);

			demos[currentDemo].PreFrame(delta, elapsed, renderCamera, new Vector2(fbW, fbH));

			engine.Render(new FrameInputs
			{
				DeltaSeconds = capturePath is not null ? captureDelta : delta,
				Time = capturePath is not null ? (captureAnimate ? frame * (float)captureDelta : captureTime) : elapsed,
			});

			if (capturePath is not null && frame >= 62)
				window.Close();
			else if (smokeFrames > 0 && frame >= smokeFrames)
			{
				// Dump the last completed frame's GPU timings (cross-backend sanity: sum ≤ frame total ≤ wall).
				if (engine.GpuTimingEnabled && engine.LastFrameTimings.Count > 0)
				{
					Console.WriteLine($"[loom] GPU timing ({engine.BackendName}, {demos[currentDemo].Name}):");
					foreach (var t in engine.LastFrameTimings)
						Console.WriteLine($"  {t.Name,-18} {t.GpuMilliseconds,7:F3} ms");
					Console.WriteLine($"  {"frame total",-18} {engine.LastFrameGpuMilliseconds,7:F3} ms");
				}
				window.Close();
			}
		};

		window.Run();
		engine.WaitIdle();
		ImGui.DestroyContext();
	}

	// Resolve which backend to run from the set actually compiled into this build (each is added under its own
	// LOOM_* constant, the same flags that pull in the backend assembly). An explicit --backend that isn't compiled
	// in is an error; with none requested we take the first compiled-in backend (Vulkan when present); a build with
	// no backend at all fails with one clear message. This replaces per-backend "not compiled in" throws scattered
	// through CreateEngine — there is a single source of truth for what's available.
	private static BackendChoice ChooseBackend(BackendChoice? requested)
	{
		var available = new List<BackendChoice>();
#if LOOM_VULKAN
		available.Add(BackendChoice.Vulkan);
#endif
#if LOOM_DIRECTX12
		available.Add(BackendChoice.DirectX12);
#endif
		if (available.Count == 0)
			throw new InvalidOperationException("No rendering backend was compiled into this build — build with -p:UseVulkan=true and/or -p:UseDirectX12=true.");
		if (requested is { } choice)
			return available.Contains(choice)
				? choice
				: throw new InvalidOperationException($"The {choice} backend was not compiled into this build (available: {string.Join(", ", available)}).");
		return available[0];
	}

	// Create the host window for the chosen backend: a Vulkan-surface window (Silk hands the backend its IVkSurface)
	// or a context-less Win32 window (the DirectX12 backend binds its swapchain to the raw HWND). The window is the only
	// place the two backends diverge before the (shared) render loop. Both option sets are plain Silk types, so this
	// needs no per-backend #if — only the engine settings below name a backend assembly.
	private static IWindow CreateWindow(BackendChoice backend, LaunchOptions opts)
	{
		var size = new Vector2D<int>(1280, 720);
		var options = backend == BackendChoice.DirectX12
			? WindowOptions.Default with { Size = size, Title = "Loom - DirectX12 demo gallery", API = GraphicsAPI.None, FramesPerSecond = opts.MaxFps }
			: WindowOptions.DefaultVulkan with { Size = size, Title = "Loom - Vulkan demo gallery", FramesPerSecond = opts.MaxFps };
		var window = Window.Create(options);
		window.Initialize();
		return window;
	}

	// Configure + create the engine for the chosen backend. This is the one place the host names a backend type: it
	// populates the matching EngineSettings subclass from the command line, hands the backend its native surface
	// (IVkSurface / HWND), and selects the registered backend by name. Constructing the settings subclass loads the
	// backend assembly, which self-registers into Engine.AvailableEngines. Each branch is gated on the same LOOM_*
	// constant ChooseBackend used, so the branch for the chosen backend is always present; the trailing throw is
	// therefore unreachable (it only guards against a future BackendChoice with no factory here).
	private static IEngine CreateEngine(BackendChoice backend, IWindow window, LaunchOptions opts)
	{
#if LOOM_VULKAN
		if (backend == BackendChoice.Vulkan)
		{
			if (window.VkSurface is null)
				throw new InvalidOperationException("The windowing platform did not expose a Vulkan surface.");
			var vk = new VulkanEngineSettings
			{
				EnableValidation = true,
				VSync = opts.VSync,
				MaxFramesPerSecond = opts.MaxFps,
				HdrOutput = opts.Hdr,
				PreferredBackend = "Vulkan",
				// Shader hot-reload (dev opt-in): watch the loose shader dir for rebuilt .spv/.json artifacts.
				WatchShaderDirectory = opts.WatchShaders,
			};
			return Resolve(window.VkSurface, window, vk);
		}
#endif
#if LOOM_DIRECTX12
		if (backend == BackendChoice.DirectX12)
		{
			var hwnd = window.Native?.Win32?.Hwnd
				?? throw new InvalidOperationException("The DirectX12 backend needs the window's Win32 HWND.");
			var dx = new DirectX12EngineSettings
			{
				EnableValidation = true,
				VSync = opts.VSync,
				MaxFramesPerSecond = opts.MaxFps,
				HdrOutput = opts.Hdr,
				PreferredBackend = "DirectX12",
				WatchShaderDirectory = opts.WatchShaders,
				// The Loom SDK copies the app shaders' signed DXIL + reflection JSON next to the exe under shaders\.
				ShaderDirectory = Path.Combine(AppContext.BaseDirectory, "shaders"),
			};
			return Resolve(hwnd, window, dx);
		}
#endif
		throw new InvalidOperationException($"No engine factory for backend {backend} in this build.");
	}

	// Create the engine on the registered backend matching the settings' PreferredBackend. The name is guaranteed
	// present (ChooseBackend only returns a compiled-in backend, and constructing its settings registered it), so a
	// miss means the backend assembly failed to self-register — a clear, rare error rather than a silent fallback.
	private static IEngine Resolve(object surface, IWindow window, EngineSettings settings)
	{
		var backend = Engine.AvailableEngines.FirstOrDefault(e => e.Name == settings.PreferredBackend)
			?? throw new InvalidOperationException($"The {settings.PreferredBackend} backend did not register into Engine.AvailableEngines.");
		return backend.Create(surface, window.FramebufferSize.X, window.FramebufferSize.Y, settings);
	}

	// Build the demo gallery: register every demo's techniques (idempotent across demos that share one), skipping any
	// whose shaders don't cross-compile on this backend/toolchain (e.g. a fragment stage with no DirectX12 DXIL on
	// win-arm64) — DefineTechniques throws NotSupportedException for the missing stage, so that demo is dropped from
	// the gallery rather than crashing the whole harness (the same spirit as the SetGraph-time capability guards).
	// This is the backend-capability filter for the menu, so it stays eager (and is cheap — no async asset I/O);
	// each demo's actual preload + graph build happens lazily, the first time it's viewed (see Main).
	private static IDemo[] BuildGallery(IEngine engine)
	{
		IDemo[] all =
		[
			new SpikeDemo(),
			new CubeLightingDemo(),
			new UIInteractiveDemo(),
			new UIGroupOpacityDemo(),
			new ShadowsDemo(),
			new SsaoDemo(),
			new TonemappingDemo(),
			new BloomDemo(),
			new ParticlesDemo(),
			new HighlightingDemo(),
			new InstancingDemo(),
			new SpritesDemo(),
			new Lighting2DDemo(),
			new SpriteAnimationDemo(),
			new TilemapDemo(),
			new ParallaxDemo(),
			new Debug2DDemo(),
			new PixelArtDemo(),
			new FullReferenceDemo(),
			new NormalMappingDemo(),
			new GltfDemo(),
			new InWorldUIDemo(),
			new SkinningDemo(),
			new CascadedShadowsDemo(),
			new SkyDayNightDemo(),
			new FoliageDemo(),
			new ToonDemo(),
			new StylizedDemo(),
			new NightGladeDemo(),
			new RuinsDemo(),
			new PixelArtCastleDemo(),
		];

		var live = new List<IDemo>();
		foreach (var demo in all)
		{
			try
			{
				demo.DefineTechniques(engine);
				live.Add(demo);
			}
			catch (NotSupportedException ex)
			{
				Console.WriteLine($"[loom] skipping demo '{demo.Name}' — {ex.Message}");
			}
		}
		return live.ToArray();
	}

	// Drive `task` to completion by draining the engine's queued texture uploads (IEngine.CreateTextureAsync) on
	// this thread — needed whenever a task can be awaiting an upload but no frame loop is running yet to service it
	// (every backend's Render also does this once per frame, for uploads requested during live rendering). Runs the
	// drain from this one call site so concurrent backends' CreateTexture calls can never race each other.
	private static async Task PumpUntilReady(IEngine engine, Task task)
	{
		while (!task.IsCompleted)
		{
			engine.ServicePendingUploads();
			await Task.Yield();
		}
		await task; // rethrow any faulting demo's exception rather than swallowing it
	}

	// Resolve the start demo from --demo into a gallery index, or -1 (having printed why) when the query can't be
	// resolved, so the caller exits cleanly. Resolution order: a 0-based index wins (unambiguous); then an exact
	// case-insensitive name (so "Bloom" picks "Bloom" even if another name contains it); then a unique
	// case-insensitive substring. A substring that matches several demos is reported as ambiguous (the matches are
	// listed) rather than guessed; one that matches none is an error (the full demo list is shown). Null = the first demo.
	private static int ResolveDemoIndex(IReadOnlyList<IDemo> demos, string? query)
	{
		if (query is null)
			return 0;

		// An explicit numeric index wins, bounds-checked.
		if (int.TryParse(query, out var index))
		{
			if (index >= 0 && index < demos.Count)
				return index;
			Console.Error.WriteLine($"[loom] --demo index {index} is out of range (0..{demos.Count - 1}).");
			return -1;
		}

		// Exact name beats substring, so an exactly-typed name is never ambiguous against a longer one.
		for (var i = 0; i < demos.Count; i++)
			if (string.Equals(demos[i].Name, query, StringComparison.OrdinalIgnoreCase))
				return i;

		// Otherwise a unique case-insensitive substring match.
		var matches = new List<int>();
		for (var i = 0; i < demos.Count; i++)
			if (demos[i].Name.Contains(query, StringComparison.OrdinalIgnoreCase))
				matches.Add(i);

		if (matches.Count == 1)
			return matches[0];
		if (matches.Count == 0)
			Console.Error.WriteLine($"[loom] --demo '{query}' matched no demo. Available: {string.Join(", ", demos.Select(d => d.Name))}.");
		else
			Console.Error.WriteLine($"[loom] --demo '{query}' is ambiguous — matched: {string.Join(", ", matches.Select(i => demos[i].Name))}. Be more specific.");
		return -1;
	}

	// Gather the currently-held modifier keys from a Silk.NET keyboard snapshot.
	private static ModifierKeys GetModifiers(IKeyboard kb)
	{
		var m = ModifierKeys.None;
		if (kb.IsKeyPressed(Key.ShiftLeft) || kb.IsKeyPressed(Key.ShiftRight)) m |= ModifierKeys.Shift;
		if (kb.IsKeyPressed(Key.ControlLeft) || kb.IsKeyPressed(Key.ControlRight)) m |= ModifierKeys.Control;
		if (kb.IsKeyPressed(Key.AltLeft) || kb.IsKeyPressed(Key.AltRight)) m |= ModifierKeys.Alt;
		return m;
	}

	// Map a Silk.NET Key to the engine-neutral VirtualKey subset (returns None for unmapped keys).
	private static VirtualKey ToVirtualKey(Key key) => key switch
	{
		Key.Backspace => VirtualKey.Back,
		Key.Tab => VirtualKey.Tab,
		Key.Enter or Key.KeypadEnter => VirtualKey.Enter,
		Key.ShiftLeft or Key.ShiftRight => VirtualKey.Shift,
		Key.ControlLeft or Key.ControlRight => VirtualKey.Control,
		Key.AltLeft or Key.AltRight => VirtualKey.Alt,
		Key.Escape => VirtualKey.Escape,
		Key.Space => VirtualKey.Space,
		Key.PageUp => VirtualKey.PageUp,
		Key.PageDown => VirtualKey.PageDown,
		Key.End => VirtualKey.End,
		Key.Home => VirtualKey.Home,
		Key.Left => VirtualKey.Left,
		Key.Up => VirtualKey.Up,
		Key.Right => VirtualKey.Right,
		Key.Down => VirtualKey.Down,
		Key.Delete => VirtualKey.Delete,
		Key.Number0 or Key.Keypad0 => VirtualKey.Number0,
		Key.Number1 or Key.Keypad1 => VirtualKey.Number1,
		Key.Number2 or Key.Keypad2 => VirtualKey.Number2,
		Key.Number3 or Key.Keypad3 => VirtualKey.Number3,
		Key.Number4 or Key.Keypad4 => VirtualKey.Number4,
		Key.Number5 or Key.Keypad5 => VirtualKey.Number5,
		Key.Number6 or Key.Keypad6 => VirtualKey.Number6,
		Key.Number7 or Key.Keypad7 => VirtualKey.Number7,
		Key.Number8 or Key.Keypad8 => VirtualKey.Number8,
		Key.Number9 or Key.Keypad9 => VirtualKey.Number9,
		Key.A => VirtualKey.A,
		Key.B => VirtualKey.B,
		Key.C => VirtualKey.C,
		Key.D => VirtualKey.D,
		Key.E => VirtualKey.E,
		Key.F => VirtualKey.F,
		Key.G => VirtualKey.G,
		Key.H => VirtualKey.H,
		Key.I => VirtualKey.I,
		Key.J => VirtualKey.J,
		Key.K => VirtualKey.K,
		Key.L => VirtualKey.L,
		Key.M => VirtualKey.M,
		Key.N => VirtualKey.N,
		Key.O => VirtualKey.O,
		Key.P => VirtualKey.P,
		Key.Q => VirtualKey.Q,
		Key.R => VirtualKey.R,
		Key.S => VirtualKey.S,
		Key.T => VirtualKey.T,
		Key.U => VirtualKey.U,
		Key.V => VirtualKey.V,
		Key.W => VirtualKey.W,
		Key.X => VirtualKey.X,
		Key.Y => VirtualKey.Y,
		Key.Z => VirtualKey.Z,
		Key.F1 => VirtualKey.F1,
		Key.F2 => VirtualKey.F2,
		Key.F3 => VirtualKey.F3,
		Key.F4 => VirtualKey.F4,
		Key.F5 => VirtualKey.F5,
		Key.F6 => VirtualKey.F6,
		Key.F7 => VirtualKey.F7,
		Key.F8 => VirtualKey.F8,
		Key.F9 => VirtualKey.F9,
		Key.F10 => VirtualKey.F10,
		Key.F11 => VirtualKey.F11,
		Key.F12 => VirtualKey.F12,
		_ => VirtualKey.None,
	};

	// Project a world-space point to framebuffer pixels (top-left origin) through a neutral view-projection
	// (frustum wireframe). Returns false for points behind the camera (w ≤ 0), so edges that cross the near plane are
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
