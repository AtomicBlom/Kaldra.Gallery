using System.Numerics;
using System.Runtime.InteropServices;

using ImGuiNET;

using Loom.Assets;
using Loom.Concepts;
using Loom.Core.Geometry;
using Loom.Core.Graph;
using Loom.Core.Hosting;
using Loom.Core.Materials;
using Loom.Gallery;
using Loom.Gallery.Demos;
using Loom.UI.SUIsei;

using SUIsei;

// Alias the painter + input-layer types only — importing the Loom.UI.ImGui namespace would shadow ImGuiNET's bare `ImGui`.
using ImGuiPainter = Loom.UI.ImGui.ImGuiPainter;
using ImGuiInputLayer = Loom.UI.ImGui.ImGuiInputLayer;

namespace Loom.Sandbox;

/// <summary>
/// The sandbox demo gallery as a <see cref="LoomApplication"/>: it owns the shared geometry, the demo roster
/// (<see cref="DemoGallery"/>), the runtime graph-swap, and every debug affordance — but the window, the engine,
/// the input pump, and the frame loop belong to the reusable desktop host (backend/presentation flags are the
/// host's <c>HostOptions</c>; the app parses only its own <c>--demo</c>/<c>--dump-graph</c>/<c>--inspect</c>/
/// <c>--frustum</c>/<c>--no-hud</c> from <see cref="LoomApplication.AppArgs"/>). One host serves both backends —
/// the gallery and its per-frame path are backend-agnostic, so the two can't drift.
/// <para>
/// Deterministic frame capture and headless smoke runs used to live here to feed golden/parity comparison; that
/// verification is now in-process offscreen tests (rendering the same <see cref="DemoGallery"/> scenes directly),
/// so the sandbox is purely the interactive host with no capture apparatus of its own.
/// </para>
/// </summary>
internal sealed class SandboxApp : LoomApplication
{
	private LaunchOptions _opts = new();
	private bool _ready;

	// Shared scene + gallery.
	private string _assetsDir = "";
	private UIHost _uiHost = null!;
	private DemoScene _scene;
	private IReadOnlyList<IDemo> _demos = [];

	// Per-demo lazy load: graphs[i]/loadTasks[i] populate the first time demo i is loaded; loadTasks caches the
	// in-flight/completed task so a repeated request (mashing Tab) awaits the same load instead of double-loading.
	private RenderGraph?[] _graphs = [];
	private Task<RenderGraph>?[] _loadTasks = [];
	private int _currentDemo;
	private int _pendingDemo;

	// The interactive UI demo, or -1 if its techniques were dropped on this backend: when it is active Tab drives
	// the UI's focus navigation instead of cycling demos.
	private int _interactiveIndex = -1;
	// Host-owned overlay page per demo (its graph composites this through scene.UI), null where the demo owns none.
	// Built once so a UI page's state (e.g. the interactive counter) persists across demo switches.
	private UIElement?[] _overlayContent = [];

	// The default perspective orbit camera (reconfigured each frame) + the detached free-fly debug camera.
	private Loom.Concepts.Camera _orbit = null!;
	private FlyCamera _fly = null!;
	private bool _pauseCamera;
	private bool _debugCam;
	private bool _debugCamInit;
	private Matrix4x4 _frozenViewProjection = Matrix4x4.Identity;
	private readonly List<Vector4> _debugSegments = []; // frustum wireframe + demo debug lines (skeleton)

	// Dear ImGui HUD — the host owns the context + per-frame UI; the Loom.UI.ImGui bridge renders its draw data
	// through the public draw-list API (no backend ImGui dependency).
	private ImGuiPainter _imguiPainter = null!;
	private ImGuiIOPtr _io;
	private readonly List<int> _inspectIds = [];
	private bool _showDemoWindow;
	private bool _showInspect;

	// The shared SUIsei demo shell (switcher, debug panel, status bar) every demo's `hud` pass composites.
	private DemoShell _shell = null!;

	// A pausable scene clock (frozen while "Pause camera" is on) driving the orbit + every demo's per-frame time.
	// Framework-owned FrameInputs.Time (the shader clock) is the host's unpausable total, so a pause holds the
	// camera and CPU-side animation while GPU-side time keeps advancing — the two coincide while never paused.
	private double _elapsed;
	private float _fpsTimer;
	private int _fpsFrames;
	private double _displayFps;
	private double _displayMs;

	private string? _dumpGraphPrefix;

	protected override void Configure(AppOptions options)
	{
		_opts = LaunchOptions.Parse(AppArgs);
		options.Title = "Loom - demo gallery";
		options.Width = 1280;
		options.Height = 720;
		// A dev harness: keep the validation/debug layer on (a shipped app defaults it off — see AppOptions).
		options.EngineSettings.EnableValidation = true;
	}

	protected override void LoadContent()
	{
		var engine = Engine;

		// Shared geometry every demo draws, uploaded once.
		var (cubeVerts, cubeIndices) = Geometry.BuildCube();
		var (planeVerts, planeIndices) = Geometry.BuildPlane();
		var (quadVerts, quadIndices) = Geometry.BuildQuad();

		// The shared brick material — albedo + tangent-space normal map, decoded from committed PNGs (loaders in
		// the neutral Loom.Assets; the engine just uploads the resulting TextureData + mip chain).
		_assetsDir = Path.Combine(AppContext.BaseDirectory, "assets");
		var brick = engine.CreateMaterial(
			engine.CreateTexture(ImageLoader.Load(Path.Combine(_assetsDir, "brick_albedo.png"), TextureFormat.Rgba8Srgb)),
			engine.CreateTexture(ImageLoader.Load(Path.Combine(_assetsDir, "brick_normal.png"), TextureFormat.Rgba8Unorm)));

		// The game-UI painter: registers "ui", uploads the white texel + default glyph atlas, renders each overlay
		// list. Created up front so the demo graphs capture it.
		_uiHost = new UIHost();
		var uiPainter = UIPainter.Create(engine, _uiHost, FontAtlas.Default);
		_scene = new DemoScene(engine.CreateMesh(cubeVerts, cubeIndices), engine.CreateMesh(planeVerts, planeIndices), engine.CreateMesh(quadVerts, quadIndices), brick, uiPainter, name => File.ReadAllBytesAsync(Path.Combine(_assetsDir, name)));

		// The gallery — one focused graph per capability. Build only registers techniques (cheap, synchronous; also
		// the backend-capability filter); each demo's actual preload + graph build is lazy (EnsureLoadedAsync).
		_demos = DemoGallery.Build(engine);

		// Start on the first (simplest) demo; --demo overrides. An unresolved query prints why and exits.
		_currentDemo = ResolveDemoIndex(_demos, _opts.Demo);
		if (_currentDemo < 0)
		{
			Exit();
			return;
		}

		_graphs = new RenderGraph?[_demos.Count];
		_loadTasks = new Task<RenderGraph>?[_demos.Count];
		_pendingDemo = _currentDemo;
		// Block until the start demo's assets have loaded — there is no frame loop yet, so this costs nothing while
		// the preload runs concurrently on background I/O/decode threads (drained here since Render isn't running).
		PumpUntilReady(engine, EnsureLoadedAsync(_currentDemo)).GetAwaiter().GetResult();

		_interactiveIndex = IndexOf("UI Interactive");
		_overlayContent = new UIElement?[_demos.Count];
		for (var i = 0; i < _demos.Count; i++)
			_overlayContent[i] = DemoGallery.OverlayContent(_demos[i]);

		_dumpGraphPrefix = _opts.DumpGraph;

		// Dear ImGui HUD. Inspection is enabled BEFORE the first SetGraph so the resource-viewer sample edges land
		// in the initial graph too.
		ImGui.CreateContext();
		_io = ImGui.GetIO();
		unsafe { _io.NativePtr->IniFilename = null; } // don't persist a layout .ini next to the exe
		_io.DisplaySize = SurfaceSize;
		_io.DisplayFramebufferScale = Vector2.One;
		_io.Fonts.AddFontDefault();
		ImGui.StyleColorsDark();
		engine.ResourceInspectionEnabled = true;
		_imguiPainter = ImGuiPainter.Create(engine);
		_showInspect = _opts.Inspect;

		_fly = new FlyCamera(engine);
		_orbit = new Loom.Concepts.Camera(engine);
		_debugCam = _opts.Frustum;

		// The shared SUIsei shell — the switcher (in its MenuBar), host toggles, and status bar. Renders identically
		// on desktop and browser; ImGui narrows to the desktop dev tools the shell's checkboxes/buttons open.
		_shell = new DemoShell([.. _demos.Select(d => d.Name)], SelectDemo);
		_shell.AddHostToggle("Pause camera", () => _pauseCamera, v => _pauseCamera = v);
		_shell.AddHostToggle("Debug camera (fly) + frustum freeze [F]", () => _debugCam, v => _debugCam = v);
		_shell.AddHostToggle("GPU timing", () => engine.GpuTimingEnabled, v => engine.GpuTimingEnabled = v);
		_shell.AddHostToggle("ImGui demo window", () => _showDemoWindow, v => _showDemoWindow = v);
		_shell.AddHostToggle("Resource viewer", () => _showInspect, v => _showInspect = v);
		_shell.AddHostAction("Export graph (DOT/JSON)", () => DumpGraph(_currentDemo, _dumpGraphPrefix ?? "loom-graph"));
		// --no-hud gates the shell chrome + the ImGui dev-tools pass — a chrome-free view matching the WebGPU backend.
		_shell.SetHudVisible(_opts.Hud);
		DemoGraph.Overlay = _opts.Hud ? _imguiPainter : null;

		// A screen-space overlay surface driven by the UIHost; its root's content is the shell, and the UI-track
		// demos' own page/panel is set into the shell's centred slot per active demo (see Draw).
		var overlay = _uiHost.CreateRoot(UILayer.Default);
		overlay.Root.Content = _shell.Root;

		engine.SetGraph(_graphs[_currentDemo]!);
		if (_dumpGraphPrefix is not null)
			DumpGraph(_currentDemo, _dumpGraphPrefix);

		// Neutral input, bottom → top: the demo pointer forwarder (drives an in-world panel's OnPointer), the SUIsei
		// overlay, the ImGui HUD, and the sandbox dev hotkeys on top — so demo-switch / debug-camera keep working
		// even while the UI has captured input. The host pumps its window's devices into QueueInput; LoomApplication
		// resolves this stack each tick.
		PushInputLayer(new DemoPointerLayer(e => _demos[_currentDemo].OnPointer(e)));
		PushInputLayer(new SUIseiInputLayer(_uiHost));
		PushInputLayer(new ImGuiInputLayer());
		PushInputLayer(new SandboxControlLayer(
			_demos.Count,
			SelectDemo,
			() => SelectDemo((_currentDemo + 1) % _demos.Count),
			() => _debugCam = !_debugCam,
			// Tab drives the UI's focus navigation while the interactive UI demo is active, so it cycles demos only otherwise.
			() => _currentDemo != _interactiveIndex));

		Console.WriteLine($"Loom - demo gallery (keys 1-{_demos.Count} / Tab) on {engine.DeviceName} ({engine.BackendName}). On Object Highlighting / Full, hover the cube. Close the window to exit.");
		_ready = true;
	}

	protected override void Update(FrameTime time)
	{
		if (!_ready)
			return;

		if (!_pauseCamera)
			_elapsed += time.DeltaSeconds;

		_fpsTimer += (float)time.DeltaSeconds;
		_fpsFrames++;
		if (_fpsTimer >= 0.25f)
		{
			_displayFps = _fpsFrames / _fpsTimer;
			_displayMs = _fpsTimer / _fpsFrames * 1000.0;
			_fpsTimer = 0f;
			_fpsFrames = 0;
		}
	}

	protected override void Draw(FrameTime time)
	{
		if (!_ready)
			return;

		var engine = Engine;
		var delta = time.DeltaSeconds;
		var fbW = (int)SurfaceSize.X;
		var fbH = (int)SurfaceSize.Y;
		var aspect = fbH == 0 ? 1f : (float)fbW / fbH;
		var camTime = (float)_elapsed;

		// Orbit the camera around the cube. Neutral projection — the backend applies the Vulkan Y-flip. A demo may
		// override with an orthographic 2D view; pulled each frame so a 2D camera can scroll.
		DemoCamera.Orbit(_orbit, camTime, aspect);
		var camera = _demos[_currentDemo].GetCamera(aspect, camTime) ?? _orbit;

		// Detached debug camera: render from a free-fly view and draw the FROZEN scripted-camera frustum as a
		// wireframe. Entering debug mode freezes the current view; its 8 corners project to screen pixels → 12 edges.
		var renderCamera = camera;
		_debugSegments.Clear();
		if (_debugCam)
		{
			if (!_debugCamInit)
			{
				_debugCamInit = true;
				_frozenViewProjection = camera.View * camera.Projection; // uncorrected world-space VP for the frustum corners
				_fly.PlaceLookingAt(new Vector3(6f, 4f, 6f), Vector3.Zero); // start outside the frozen frustum
			}
			renderCamera = _fly.Update(Input, (float)delta, aspect);

			var corners = _frozenViewProjection.GetFrustumCorners();
			var vp = renderCamera.View * renderCamera.Projection;
			var edges = Frustum.EdgeIndices;
			for (var e = 0; e < edges.Length; e += 2)
				if (ProjectToScreen(corners[edges[e]], vp, fbW, fbH, out var p0) &&
					ProjectToScreen(corners[edges[e + 1]], vp, fbW, fbH, out var p1))
					_debugSegments.Add(new Vector4(p0.X, p0.Y, p1.X, p1.Y));
		}
		else
		{
			_debugCamInit = false;
		}

		// The active demo's world-space debug lines (e.g. the skinning skeleton), projected against the render camera.
		var demoLines = _demos[_currentDemo].DebugWorldLines((float)_elapsed);
		if (demoLines.Count > 0)
		{
			var vp = renderCamera.View * renderCamera.Projection;
			for (var i = 0; i < demoLines.Count; i++)
				if (ProjectToScreen(demoLines[i].A, vp, fbW, fbH, out var a) &&
					ProjectToScreen(demoLines[i].B, vp, fbW, fbH, out var b))
					_debugSegments.Add(new Vector4(a.X, a.Y, b.X, b.Y));
		}
		_imguiPainter.SetDebugLines(CollectionsMarshal.AsSpan(_debugSegments));

		// Hover (Object Highlighting / Full): read the demo's object-id buffer back at the cursor and hand the id
		// to the demo, which writes it into the outline's parameter buffer. A demo with no PickTarget skips this —
		// the engine carries no picking state of its own.
		uint hoverId = 0u;
		if (_demos[_currentDemo].PickTarget is { } pickTarget)
		{
			var pointer = Input.PointerPosition;
			var (pw, ph) = pickTarget.Resolve();
			var px = (int)(pointer.X / Math.Max(fbW, 1) * pw);
			var py = (int)(pointer.Y / Math.Max(fbH, 1) * ph);
			var pixels = MemoryMarshal.Cast<byte, Half>(engine.Read(pickTarget));
			var i = py * pw + px;
			if ((uint)i < (uint)pixels.Length)
				hoverId = (uint)MathF.Round((float)pixels[i]);
			_demos[_currentDemo].WriteHover(engine, hoverId);
		}

		// Drive ImGui: the ImGuiInputLayer already fed this frame's mouse/keyboard/text into the IO during input
		// resolution; refresh the per-frame display config, then build the bar. The bridge renders the resulting
		// draw data in the hud pass (it reads ImGui.GetDrawData() from the pass closure).
		_io.DisplaySize = SurfaceSize;
		_io.DisplayFramebufferScale = Vector2.One;
		_io.DeltaTime = delta > 0 ? (float)delta : 1f / 60f;
		ImGui.NewFrame();

		// Refresh the shell's per-frame bits: the active demo's name + debug panel, the engine name, the smoothed
		// fps, and the status line (hovered id + the demo's own HudStatus).
		_shell.SetActiveDemo(_demos[_currentDemo].Name, _demos[_currentDemo].DebugPanel);
		_shell.SetEngineName(engine.DeviceName);
		_shell.SetFps((float)_displayFps, (float)_displayMs);
		_shell.SetStatus($"hovered {hoverId}");
		_shell.SetStatusRight(_demos[_currentDemo].HudStatus);

		if (_showDemoWindow)
			ImGui.ShowDemoWindow(ref _showDemoWindow);

		// Resource viewer: enumerate the active graph's colour/depth resources; the bridge draws the matching
		// thumbnail grid (row-major, same order) via the public draw-list API (ImGui.Image can't on win-arm64).
		_inspectIds.Clear();
		if (_showInspect)
		{
			if (ImGui.Begin("Resource viewer", ref _showInspect))
			{
				ImGui.Text("Buffers (grid is row-major, same order):");
				var slot = 0;
				foreach (var r in engine.InspectableResources)
				{
					_inspectIds.Add(r.Id);
					ImGui.Text($"{slot++,2}  {r.Name}  ({r.Kind} {r.Format})");
				}
			}
			ImGui.End();
		}
		_imguiPainter.SetThumbnails(CollectionsMarshal.AsSpan(_inspectIds));

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
		// input → hit-tests → routes → synthesizes Click → lays out; Render composites each view → DrawLists. The
		// "hud" pass renders the whole shell (chrome + centred content) via DemoGraph.Hud.
		_shell.SetCenterContent(_overlayContent[_currentDemo]);
		_uiHost.Tick(delta, (float)_elapsed, SurfaceSize);
		_uiHost.Render();

		// Per-frame CPU work for the active demo — after any mid-frame demo switch via the shell menu (which routes
		// during uiHost.Tick above), so the current demo's Update always fires before its graph records this frame.
		_demos[_currentDemo].Update(camera);
		_demos[_currentDemo].PreFrame(delta, (float)_elapsed, renderCamera, new Vector2(fbW, fbH));
	}

	protected override void UnloadContent()
	{
		DemoGraph.Overlay = null; // release the process-global ImGui overlay hook before the context is destroyed
		if (_ready)
			ImGui.DestroyContext();
	}

	// ── Demo load / activate / switch ───────────────────────────────────────────────────────────────────────

	private Task<RenderGraph> EnsureLoadedAsync(int index) => _loadTasks[index] ??= LoadAsync(index);

	private async Task<RenderGraph> LoadAsync(int index)
	{
		await _demos[index].PreloadAsync(_scene);
		var graph = _demos[index].Build(_scene);
		_graphs[index] = graph;
		return graph;
	}

	// Swap in an already-loaded demo's graph. currentDemo (and therefore every _demos[_currentDemo] read in Draw)
	// only changes here, once the new demo's graph exists, so the previous demo keeps rendering while a switch loads.
	private void ActivateDemo(int index, RenderGraph graph)
	{
		_currentDemo = index;
		Engine.SetGraph(graph); // drains the GPU, releases the old transients, binds the new graph
		Console.WriteLine($"[loom] demo → {_demos[index].Name}");
		if (_dumpGraphPrefix is not null)
			DumpGraph(index, _dumpGraphPrefix);
	}

	// Fire-and-forget switch: loads (or reuses the cached load of) the target demo, then activates it — unless a
	// later switch has since superseded this one (pendingDemo no longer matches), in which case the result is
	// discarded. The per-frame Render drains ServicePendingUploads, so no explicit pump is needed once running.
	private async Task SwitchDemoAsync(int index)
	{
		_pendingDemo = index;
		var graph = await EnsureLoadedAsync(index);
		if (_pendingDemo == index)
			ActivateDemo(index, graph);
	}

	private void SelectDemo(int index)
	{
		if (index < 0 || index >= _demos.Count || index == _currentDemo || index == _pendingDemo)
			return;
		_ = SwitchDemoAsync(index);
	}

	private int IndexOf(string demoName)
	{
		for (var i = 0; i < _demos.Count; i++)
			if (_demos[i].Name == demoName)
				return i;
		return -1;
	}

	// --dump-graph=<prefix> writes "<prefix>.<demo>.dot" (data-flow) + ".json" for the active graph on startup and
	// every swap; the shell's "Export graph" item writes the same on demand.
	private void DumpGraph(int index, string prefix)
	{
		// Only ever called with an already-loaded index (the start demo, or a demo just activated).
		var graph = _graphs[index] ?? throw new InvalidOperationException($"DumpGraph called on unloaded demo '{_demos[index].Name}'.");
		var safe = string.Concat(_demos[index].Name.Split(Path.GetInvalidFileNameChars())).Replace(' ', '_');
		var dot = $"{prefix}.{safe}.dot";
		var json = $"{prefix}.{safe}.json";
		File.WriteAllText(dot, graph.ToDot());
		File.WriteAllText(json, graph.ToJson());
		Console.WriteLine($"[loom] graph → {dot} + {json}");
	}

	// Drive `task` to completion by draining the engine's queued texture uploads on this thread — needed while no
	// frame loop is running yet to service them (every backend's Render drains once per frame otherwise).
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
	// case-insensitive substring. Ambiguous → reported and listed; no match → error (the full list is shown).
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

	// Project a world-space point to framebuffer pixels (top-left origin) through a neutral view-projection. Returns
	// false for points behind the camera (w ≤ 0), so edges that cross the near plane are dropped rather than wrapped.
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
