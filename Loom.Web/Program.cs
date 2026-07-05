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

	// The demo shaders (lit, gbuffer, …) are embedded in this WASM assembly as WGSL under "Loom.Web.Shaders.";
	// resolve them by artifact name so the backend finds them alongside its own engine shaders.
	var settings = new WebGpuEngineSettings { EnableValidation = true, ShaderResolver = ResolveEmbeddedShader };
	var backend = Engine.AvailableEngines.First(e => e.Name == "WebGPU");
	// Render at the canvas's real display resolution (CSS size × devicePixelRatio) so the output fills the window
	// crisply — the engine's size, the canvas backing store, and the viewport all agree.
	var (width, height) = ParseSize(Host.GetCanvasSize("#loom-canvas"));
	var engine = backend.Create(surface, width, height, settings);

	// First real 3D scene on the WebGPU backend: a lit cube + ground plane with a depth buffer and an orbiting
	// camera — exercising meshes, per-draw + constants uniforms, and the depth path end to end.
	var demo = new CubeLightingDemo(engine);
	engine.SetGraph(demo.Build(width, height));

	App.Attach(engine, demo);
	Host.SetStatus($"Loom running on {engine.DeviceName} ({width}×{height})");
	Host.StartFrameLoop(App.Frame);
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

// Parse the "w,h" device-pixel size the JS side reports for the canvas.
static (int Width, int Height) ParseSize(string wh)
{
	var parts = wh.Split(',');
	return parts.Length == 2 && int.TryParse(parts[0], out var w) && int.TryParse(parts[1], out var h)
		? (Math.Max(1, w), Math.Max(1, h))
		: (1280, 720);
}

/// <summary>The per-frame driver: the browser's requestAnimationFrame calls <see cref="Frame"/>, which advances
/// time and renders one Loom frame.</summary>
internal static class App
{
	private static IEngine? _engine;
	private static CubeLightingDemo? _demo;
	private static double _lastTimestampMs;
	private static float _time;

	public static void Attach(IEngine engine, CubeLightingDemo demo)
	{
		_engine = engine;
		_demo = demo;
	}

	private static bool _faulted;

	public static void Frame(double timestampMs)
	{
		if (_engine is null || _demo is null || _faulted)
			return;
		var delta = _lastTimestampMs > 0 ? (timestampMs - _lastTimestampMs) / 1000.0 : 1.0 / 60.0;
		_lastTimestampMs = timestampMs;
		_time += (float)delta;
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
}
