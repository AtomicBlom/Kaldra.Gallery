using System.Linq;
using System.Numerics;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Runtime.InteropServices.JavaScript;
using System.Text;

using Loom.Assets;
using Loom.Backend.WebGPU;
using Loom.Concepts;
using Loom.Core.Graph;
using Loom.Core.Hosting;
using Loom.Core.Materials;
using Loom.Gallery.Demos;
using Loom.UI.SUIsei;
using Loom.Web.Demos;

using SUIsei;
using SUIsei.Input;

// The "loom-webgpu" module (its exports) is registered by main.js via setModuleImports before Main runs, so
// the backend's and this host's [JSImport]s resolve without an ImportAsync round-trip. Any startup failure is
// surfaced to the on-page status line (the browser console is hard to read in headless verification runs).
try
{
	await BootAsync();
}
catch (Exception ex)
{
	var formatted = App.FormatError("ERROR", ex);
	Host.SetStatus(formatted);
	Host.ConsoleError(formatted);
	throw;
}

async Task BootAsync()
{
	if (!Host.IsWebGpuAvailable())
	{
		Host.SetStatus("WebGPU is not available in this browser. Try a recent Chrome or Edge.");
		return;
	}

	// Bring the device up (async) before creating the engine — every later WebGPU call is synchronous, so the
	// engine contract (a synchronous IRenderBackend.Create + Render) holds unchanged.
	var capsJson = await Host.InitDevice("#loom-canvas", WebGpuProtocol.Version);
	var surface = WebGpuSurface.FromCapsJson("#loom-canvas", capsJson);

	// Capture mode (?capture=<name>): render on a frozen clock and grab frame 60, matching the desktop --capture
	// protocol so the two are pixel-comparable. &post routes the PNG to the diff harness's server instead of a download.
	var query = Host.GetLocationSearch();
	var captureName = QueryValue(query, "capture");
	var captureViaPost = query.Contains("post", StringComparison.Ordinal);

	// The demo shaders (lit, gbuffer, …) are embedded in this WASM assembly as WGSL under "Loom.Web.Shaders.";
	// resolve them by artifact name so the backend finds them alongside its own engine shaders.
	var settings = new WebGpuEngineSettings { EnableValidation = true, ShaderResolver = ResolveEmbeddedShader, CaptureViaPost = captureViaPost };
	// Construct the one browser backend directly rather than discovering it through Backends.Available: that
	// registry is populated by the backend's [ModuleInitializer], and module initializers are not reliably run under
	// WASM AOT + trimming (the registry comes up empty and the lookup throws). There is exactly one backend here.
	var backend = new WebGpuBackend();
	var (width, height) = captureName is not null
		? (App.CaptureWidth, App.CaptureHeight)
		: App.ParseSize(Host.GetCanvasSize("#loom-canvas"));
	var engine = backend.Create(surface, width, height, settings);

	// The shared, app-owned resources every demo draws — the same set the desktop sandbox uploads (meshes, the brick
	// material, a UIHost + painter for the overlay), plus the async asset resolver (an HTTP fetch — the browser has
	// no filesystem). Handed to each demo's Build so a demo authors identically on both hosts. Awaited directly
	// (not through the pump below) since nothing else touches the engine yet — this is the one moment startup is
	// genuinely linear, with no concurrent GPU-creating call to race.
	var (cubeVerts, cubeIndices) = Geometry.BuildCube();
	var (planeVerts, planeIndices) = Geometry.BuildPlane();
	var (quadVerts, quadIndices) = Geometry.BuildQuad();
	var brick = engine.CreateMaterial(
		engine.CreateTexture(ImageLoader.Decode(await WebAssets.LoadAsync("brick_albedo.png"), TextureFormat.Rgba8Srgb)),
		engine.CreateTexture(ImageLoader.Decode(await WebAssets.LoadAsync("brick_normal.png"), TextureFormat.Rgba8Unorm)));
	var uiHost = new UIHost();
	var uiPainter = UIPainter.Create(engine, uiHost, FontAtlas.Default);
	var scene = new DemoScene(
		engine.CreateMesh(cubeVerts, cubeIndices),
		engine.CreateMesh(planeVerts, planeIndices),
		engine.CreateMesh(quadVerts, quadIndices),
		brick, uiPainter, WebAssets.LoadAsync);

	// Select the demo from ?demo= (or the capture name, so the harness selects a demo by capturing it); default cube.
	var demoKey = WebGallery.Resolve(QueryValue(query, "demo") ?? captureName);
	var demo = WebGallery.Create(demoKey);
	demo.DefineTechniques(engine);
	// No frame loop is running yet (it starts below), so nothing else drains the engine's queued texture/mesh
	// uploads a PreloadAsync might await — pump it explicitly until the preload resolves.
	await App.PumpUntilReady(engine, demo.PreloadAsync(scene));
	var graph = demo.Build(scene);
	engine.SetGraph(graph);

	// The shared SUIsei demo shell — the same switcher/debug-panel/status chrome the desktop sandbox renders,
	// giving the browser a real demo switcher for the first time (App.SwitchDemo drives it: define techniques,
	// rebuild the graph, swap the shell's active-demo state). Capture mode hides the chrome (matching the desktop
	// --no-hud convention the Vulkan references were captured with) but never a UI demo's own centred content —
	// that content is the demo's subject, not chrome. The menu shows each demo's display Name (not its ?demo= key),
	// matching the desktop switcher; a throwaway instance per key is cheap (construction does no engine work).
	var demoEntries = WebGallery.Names.Select(key => (Key: key, Name: WebGallery.Create(key).Name)).ToArray();
	var shell = new DemoShell([.. demoEntries.Select(e => e.Name)], index => App.SwitchDemo(demoEntries[index].Key, engine, scene));
	shell.SetHudVisible(captureName is null);
	shell.SetEngineName(engine.DeviceName);
	shell.SetActiveDemo(demo.Name, demo.DebugPanel);
	shell.SetCenterContent(WebGallery.OverlayContent(demoKey));

	// The shell IS the overlay root's content; a UI demo's own page/panel lives in the shell's centred slot.
	var overlay = uiHost.CreateRoot(UILayer.Default);
	overlay.Root.Content = shell.Root;

	App.Attach(engine, demo, graph, uiHost, shell, demoKey, captureName, width, height);
	Host.SetStatus($"Loom running on {engine.DeviceName} ({width}×{height})");
	Host.StartFrameLoop(App.Frame);
	Host.StartResizeHandler(App.Resize);
	Host.StartPointerHandler(App.Pointer);
}

// Extract a query-string value (e.g. "?capture=cube&post" → QueryValue(…, "capture") == "cube").
static string? QueryValue(string query, string key)
{
	foreach (var pair in query.TrimStart('?').Split('&', StringSplitOptions.RemoveEmptyEntries))
	{
		var eq = pair.IndexOf('=');
		if (eq > 0 && pair[..eq] == key)
			return Uri.UnescapeDataString(pair[(eq + 1)..]);
	}
	return null;
}

// Resolve a demo shader artifact (e.g. "lit.wgsl" / "lit.wgsl.json") from this assembly's embedded resources.
static byte[]? ResolveEmbeddedShader(string nameWithExtension)
{
	var assembly = Assembly.GetExecutingAssembly();
	using var stream = assembly.GetManifestResourceStream($"Loom.Web.Shaders.{nameWithExtension}");
	if (stream is null)
		return null;
	var bytes = new byte[stream.Length];
	stream.ReadExactly(bytes);
	return bytes;
}

/// <summary>The per-frame driver: the browser's requestAnimationFrame calls <see cref="Frame"/>, which advances the
/// clock, drives the active <see cref="IDemo"/> exactly as the desktop launcher does (shared orbit camera, per-frame
/// update, UI tick, cursor picking), and renders one Loom frame.</summary>
internal static class App
{
	/// <summary>Parse the "w,h" device-pixel size the JS side reports for the canvas.</summary>
	public static (int Width, int Height) ParseSize(string wh)
	{
		var parts = wh.Split(',');
		return parts.Length == 2 && int.TryParse(parts[0], out var w) && int.TryParse(parts[1], out var h)
			? (Math.Max(1, w), Math.Max(1, h))
			: (1280, 720);
	}

	private static IEngine? _engine;
	private static IDemo? _demo;
	private static UIHost? _uiHost;
	private static DemoShell? _shell;
	private static Camera? _orbit;
	private static string _demoKey = "cube";
	private static Vector2? _captureClickPosition;
	private static double _lastTimestampMs;
	private static float _time;
	private static float _smoothedFps;
	private static Vector2 _pointer;
	private static int _width = CaptureWidth;
	private static int _height = CaptureHeight;

	// Capture mode: a frozen clock (fixed 1/60 s per frame) and a one-shot grab at frame 60, so the browser capture
	// matches the deterministic desktop --capture frame for a pixel diff.
	private static string? _captureName;
	private const float CaptureDelta = 1f / 60f;
	private const int CaptureFrame = 60;
	private const int CaptureClickFrame = 30;
	private static int _frame;

	/// <summary>The fixed render resolution used in capture mode — the desktop sandbox's <c>--capture</c> framebuffer
	/// size, so a browser capture is pixel-comparable with the Vulkan reference no matter the headless viewport.</summary>
	public const int CaptureWidth = 1280;
	public const int CaptureHeight = 720;

	// Format an exception (and its InnerException chain) with a full stack trace, for the on-page status line
	// and the devtools console — the default "TypeName: Message" alone isn't enough to locate a fault from the
	// edge debugger.
	public static string FormatError(string prefix, Exception ex)
	{
		var sb = new StringBuilder();
		sb.Append(prefix).Append(": ");
		for (var e = ex; e is not null; e = e.InnerException)
		{
			if (e != ex)
				sb.Append("\n--- inner exception ---\n");
			sb.Append(e.GetType().Name).Append(": ").Append(e.Message).Append('\n').Append(e.StackTrace);
		}
		return sb.ToString();
	}

	// Demos already loaded (preloaded + built) at least once, keyed by ?demo= key, so revisiting one is instant
	// instead of re-fetching/re-decoding its assets from scratch.
	private static readonly Dictionary<string, (IDemo Demo, RenderGraph Graph)> _loaded = new(StringComparer.OrdinalIgnoreCase);

	public static void Attach(IEngine engine, IDemo demo, RenderGraph graph, UIHost uiHost, DemoShell shell, string demoKey, string? captureName, int width, int height)
	{
		_engine = engine;
		_demo = demo;
		_uiHost = uiHost;
		_shell = shell;
		_orbit = new Camera(engine);
		_demoKey = demoKey;
		_loaded[demoKey] = (demo, graph);
		_captureClickPosition = WebGallery.CaptureClickPosition(demoKey);
		_captureName = captureName;
		_width = width;
		_height = height;
	}

	// Tracks the most recently *requested* switch, so a slower-loading switch that's since been superseded by
	// another click doesn't clobber it when its preload finally resolves.
	private static string? _pendingDemoKey;

	/// <summary>Switch the active demo at runtime — the shell's "Demos" menu calls this. Fire-and-forget: the frame
	/// loop keeps rendering the current demo while the new one preloads (its own per-frame ServicePendingUploads
	/// call drains whatever the preload queues — no explicit pump needed here, unlike the very first demo at boot,
	/// because that loop is already running), so a switch never shows a blank/frozen frame while it loads.</summary>
	public static void SwitchDemo(string key, IEngine engine, DemoScene scene)
	{
		if (_shell is null || key == _demoKey)
			return;
		_pendingDemoKey = key;
		_ = SwitchDemoAsync(key, engine, scene);
	}

	private static async Task SwitchDemoAsync(string key, IEngine engine, DemoScene scene)
	{
		if (!_loaded.TryGetValue(key, out var cached))
		{
			var demo = WebGallery.Create(key);
			demo.DefineTechniques(engine);
			await demo.PreloadAsync(scene);
			if (_pendingDemoKey != key)
				return; // superseded by a later switch while this one was still loading
			cached = (demo, demo.Build(scene));
			_loaded[key] = cached;
		}
		engine.SetGraph(cached.Graph); // drains the GPU, releases the old transients, binds the new graph
		_demo = cached.Demo;
		_demoKey = key;
		_captureClickPosition = WebGallery.CaptureClickPosition(key);
		_shell?.SetActiveDemo(cached.Demo.Name, cached.Demo.DebugPanel);
		_shell?.SetCenterContent(WebGallery.OverlayContent(key));
	}

	/// <summary>Drive <paramref name="task"/> to completion by draining the engine's queued GPU-creation calls
	/// (<see cref="IEngine.ServicePendingUploads"/>) on this thread — needed only when no frame loop is running yet
	/// to service them (the very first demo at boot; every later switch rides the already-running frame loop).</summary>
	public static async Task PumpUntilReady(IEngine engine, Task task)
	{
		while (!task.IsCompleted)
		{
			engine.ServicePendingUploads();
			await Task.Yield();
		}
		await task;
	}

	private static bool _faulted;

	/// <summary>Re-fit the render resolution to the canvas's new display size after a window resize.</summary>
	public static void Resize()
	{
		// In capture mode the render size is pinned to the reference framebuffer size; ignore viewport resizes so
		// the captured frame stays pixel-comparable with the desktop reference.
		if (_engine is null || _demo is null || _faulted || _captureName is not null)
			return;
		var (width, height) = ParseSize(Host.GetCanvasSize("#loom-canvas"));
		try
		{
			_engine.Resize(width, height);
			_width = width;
			_height = height;
		}
		catch (Exception ex)
		{
			_faulted = true;
			var formatted = App.FormatError("RESIZE ERROR", ex);
			Host.SetStatus(formatted);
			Host.ConsoleError(formatted);
		}
	}

	public static void Frame(double timestampMs)
	{
		if (_engine is null || _demo is null || _uiHost is null || _orbit is null || _faulted)
			return;

		double delta;
		if (_captureName is not null)
		{
			// Frozen clock: advance a fixed step per frame so the captured frame is deterministic.
			delta = CaptureDelta;
			_time = _frame * CaptureDelta;
		}
		else
		{
			delta = _lastTimestampMs > 0 ? (timestampMs - _lastTimestampMs) / 1000.0 : 1.0 / 60.0;
			_lastTimestampMs = timestampMs;
			_time += (float)delta;
		}

		try
		{
			var (w, h) = (_width, _height);
			var aspect = h > 0 ? w / (float)h : 16f / 9f;

			// The frame's camera: the demo may override (a 2D/sprite demo returns an orthographic view), otherwise the
			// shared orbit — the same one the desktop launcher renders from, so a capture matches the Vulkan reference.
			var camera = _demo.GetCamera(aspect, _time) ?? OrbitCamera(aspect);

			// The overlay UIHost: drain input → hit-test → layout → composite into the draw list the demo's ui pass
			// renders. Deterministic capture injects one click at the demo's chosen screen position at a fixed frame,
			// routed through both the shell's UIHost and the demo's own OnPointer — exactly like a real DOM pointer
			// event (App.Pointer, below) — so a demo whose UI lives outside the shell (InWorldUIDemo's world-space
			// panel) still gets exercised. This can trigger a mid-frame demo switch (the shell's menu calls
			// App.SwitchDemo), so the per-demo CPU work below runs after it — otherwise a freshly switched demo's
			// graph would record with a closure that reads a field only Update() sets (e.g. FullReferenceDemo._cam),
			// before Update() ever ran on it.
			if (_captureName is not null && _captureClickPosition is { } clickPos && _frame == CaptureClickFrame)
			{
				_uiHost.QueuePointer(PointerInput.Press(clickPos));
				_uiHost.QueuePointer(PointerInput.Release(clickPos));
				_demo.OnPointer(PointerInput.Press(clickPos));
				_demo.OnPointer(PointerInput.Release(clickPos));
			}
			_uiHost.Tick((float)delta, _time, new Vector2(w, h));
			_uiHost.Render();

			// Per-frame CPU work for the active demo — runs here, after any mid-frame demo switch, so the current
			// demo's Update always fires before its graph records (prevents null closures).
			_demo.Update(camera);
			_demo.PreFrame(delta, _time, camera, new Vector2(w, h));

			// Cursor picking (Object Highlighting): read the demo's object-id buffer back at the pointer and hand the
			// hovered id to the demo, which writes it into its outline params. In capture there is no cursor, so force
			// the cube hovered (id 1) — matching the desktop --capture protocol — so the outline shows deterministically.
			var hoverId = 0u;
			if (_demo.PickTarget is { } pickTarget)
			{
				hoverId = _captureName is not null ? 1u : ReadHover(pickTarget, w, h);
				_demo.WriteHover(_engine, hoverId);
			}

			if (_captureName is not null && _frame == CaptureFrame)
				_engine.RequestCapture(_captureName); // late enough that the delta-driven auto-exposure has converged

			// Refresh the shell's live status: an exponentially-smoothed fps (delta already accounts for the frozen
			// capture clock, so this stays deterministic in capture mode too) and the hovered id.
			var instantFps = delta > 0 ? (float)(1.0 / delta) : 0f;
			_smoothedFps = _smoothedFps <= 0f ? instantFps : _smoothedFps * 0.9f + instantFps * 0.1f;
			_shell?.SetFps(_smoothedFps, (float)(delta * 1000.0));
			_shell?.SetStatus($"hovered {hoverId}");
			_shell?.SetStatusRight(_demo.HudStatus);

			_engine.Render(new FrameInputs { DeltaSeconds = delta, Time = _time });
		}
		catch (Exception ex)
		{
			_faulted = true;
			var formatted = FormatError("RENDER ERROR", ex);
			Host.SetStatus(formatted);
			Host.ConsoleError(formatted);
		}
		_frame++;
	}

	// Point the reused orbit camera at the origin for this frame (the demo's fallback camera).
	private static Camera OrbitCamera(float aspect)
	{
		DemoCamera.Orbit(_orbit!, _time, aspect);
		return _orbit!;
	}

	// Decode the hovered object id from the picking buffer at the last pointer position. The id target stores ids as
	// Half; map the pointer through the target's own resolution, exactly as the desktop launcher does.
	private static uint ReadHover(ResourceHandle pickTarget, int fbWidth, int fbHeight)
	{
		var (pw, ph) = pickTarget.Resolve();
		var px = (int)(_pointer.X / Math.Max(fbWidth, 1) * pw);
		var py = (int)(_pointer.Y / Math.Max(fbHeight, 1) * ph);
		var pixels = MemoryMarshal.Cast<byte, Half>(_engine!.Read(pickTarget));
		var i = py * pw + px;
		return (uint)i < (uint)pixels.Length ? (uint)MathF.Round((float)pixels[i]) : 0u;
	}

	/// <summary>Forward a DOM pointer event (device pixels; phase 0 = move, 1 = down, 2 = up) to the overlay + demo.</summary>
	public static void Pointer(double x, double y, double phase)
	{
		if (_uiHost is null || _demo is null || _faulted)
			return;
		_pointer = new Vector2((float)x, (float)y);
		var e = (int)phase switch
		{
			1 => PointerInput.Press(_pointer),
			2 => PointerInput.Release(_pointer),
			_ => PointerInput.Move(_pointer),
		};
		_uiHost.QueuePointer(e);
		_demo.OnPointer(e);
	}
}

/// <summary>Host-side JS interop: device bring-up, status text, and the frame loop — the pieces the browser
/// host owns (the backend owns the GPU-object and per-frame command interop).</summary>
internal static partial class Host
{
	[JSImport("isWebGpuAvailable", "loom-webgpu")]
	public static partial bool IsWebGpuAvailable();

	[JSImport("initDevice", "loom-webgpu")]
	public static partial Task<string> InitDevice(string canvasSelector, int protocolVersion);

	/// <summary>Fetch a demo asset's bytes (base64-encoded — see loom-webgpu.js's fetchAsset for why) over HTTP,
	/// relative to the served root (e.g. "assets/rigged_bar.glb") — the browser counterpart to a desktop file read.
	/// Backs <see cref="Loom.Web.Demos.WebAssets.LoadAsync"/>, which decodes the base64 back to bytes.</summary>
	[JSImport("fetchAsset", "loom-webgpu")]
	public static partial Task<string> FetchAssetBase64(string url);

	[JSImport("setStatus", "loom-webgpu")]
	public static partial void SetStatus(string text);

	[JSImport("consoleError", "loom-webgpu")]
	public static partial void ConsoleError(string text);

	[JSImport("getCanvasSize", "loom-webgpu")]
	public static partial string GetCanvasSize(string canvasSelector);

	[JSImport("getLocationSearch", "loom-webgpu")]
	public static partial string GetLocationSearch();

	[JSImport("startFrameLoop", "loom-webgpu")]
	public static partial void StartFrameLoop([JSMarshalAs<JSType.Function<JSType.Number>>] Action<double> onFrame);

	[JSImport("startResizeHandler", "loom-webgpu")]
	public static partial void StartResizeHandler([JSMarshalAs<JSType.Function>] Action onResize);

	[JSImport("startPointerHandler", "loom-webgpu")]
	public static partial void StartPointerHandler([JSMarshalAs<JSType.Function<JSType.Number, JSType.Number, JSType.Number>>] Action<double, double, double> onPointer);
}
