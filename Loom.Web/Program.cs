using System.Reflection;
using System.Runtime.InteropServices.JavaScript;

using Loom.Backend.WebGPU;
using Loom.Core.Hosting;
using Loom.Web.Demos;

// The "loom-webgpu" module (its exports) is registered by main.js via setModuleImports before Main runs, so
// the backend's and this host's [JSImport]s resolve without an ImportAsync round-trip. Any startup failure is
// surfaced to the on-page status line (the browser console is hard to read in headless verification runs).
try
{
	await BootAsync();
}
catch (Exception ex)
{
	Host.SetStatus($"ERROR: {ex.GetType().Name}: {ex.Message}");
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
	var backend = Engine.AvailableEngines.First(e => e.Name == "WebGPU");
	// Render at the canvas's real display resolution (CSS size × devicePixelRatio) so the output fills the window
	// crisply — the engine's size, the canvas backing store, and the viewport all agree.
	var (width, height) = App.ParseSize(Host.GetCanvasSize("#loom-canvas"));
	var engine = backend.Create(surface, width, height, settings);

	// Pick the demo from ?demo= (or the capture name, so the harness selects a demo by capturing it); default cube.
	var demoName = QueryValue(query, "demo") ?? captureName;
	var demo = WebDemos.Create(demoName, engine);
	engine.SetGraph(demo.Build(width, height));

	App.Attach(engine, demo, captureName);
	Host.SetStatus($"Loom running on {engine.DeviceName} ({width}×{height})");
	Host.StartFrameLoop(App.Frame);
	Host.StartResizeHandler(App.Resize);
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

/// <summary>The per-frame driver: the browser's requestAnimationFrame calls <see cref="Frame"/>, which advances
/// time and renders one Loom frame.</summary>
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
	private static IWebDemo? _demo;
	private static double _lastTimestampMs;
	private static float _time;

	// Capture mode: a frozen clock (fixed 1/60 s per frame) and a one-shot grab at frame 60, so the browser capture
	// matches the deterministic desktop --capture frame for a pixel diff.
	private static string? _captureName;
	private const float CaptureDelta = 1f / 60f;
	private const int CaptureFrame = 60;
	private static int _frame;

	public static void Attach(IEngine engine, IWebDemo demo, string? captureName)
	{
		_engine = engine;
		_demo = demo;
		_captureName = captureName;
	}

	private static bool _faulted;

	/// <summary>Re-fit the render resolution and camera aspect to the canvas's new display size after a window resize.</summary>
	public static void Resize()
	{
		if (_engine is null || _demo is null || _faulted)
			return;
		var (width, height) = ParseSize(Host.GetCanvasSize("#loom-canvas"));
		try
		{
			_engine.Resize(width, height);
			_demo.Resize(width, height);
		}
		catch (Exception ex)
		{
			_faulted = true;
			Host.SetStatus($"RESIZE ERROR: {ex.GetType().Name}: {ex.Message}");
		}
	}

	public static void Frame(double timestampMs)
	{
		if (_engine is null || _demo is null || _faulted)
			return;

		double delta;
		if (_captureName is not null)
		{
			// Frozen clock: advance a fixed step per frame so the captured frame is deterministic.
			delta = CaptureDelta;
			_time = _frame * CaptureDelta;
			if (_frame == CaptureFrame)
				_engine.RequestCapture(_captureName);
		}
		else
		{
			delta = _lastTimestampMs > 0 ? (timestampMs - _lastTimestampMs) / 1000.0 : 1.0 / 60.0;
			_lastTimestampMs = timestampMs;
			_time += (float)delta;
		}

		try
		{
			_demo.Update(_time);
			_engine.Render(new FrameInputs { DeltaSeconds = delta, Time = _time });
		}
		catch (Exception ex)
		{
			_faulted = true;
			Host.SetStatus($"RENDER ERROR: {ex.GetType().Name}: {ex.Message}");
		}
		_frame++;
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

	[JSImport("setStatus", "loom-webgpu")]
	public static partial void SetStatus(string text);

	[JSImport("getCanvasSize", "loom-webgpu")]
	public static partial string GetCanvasSize(string canvasSelector);

	[JSImport("getLocationSearch", "loom-webgpu")]
	public static partial string GetLocationSearch();

	[JSImport("startFrameLoop", "loom-webgpu")]
	public static partial void StartFrameLoop([JSMarshalAs<JSType.Function<JSType.Number>>] Action<double> onFrame);

	[JSImport("startResizeHandler", "loom-webgpu")]
	public static partial void StartResizeHandler([JSMarshalAs<JSType.Function>] Action onResize);
}
