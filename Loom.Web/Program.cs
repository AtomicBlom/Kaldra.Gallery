using System.Numerics;
using System.Runtime.InteropServices.JavaScript;

using Loom.Backend.WebGPU;
using Loom.Core.Graph;
using Loom.Core.Hosting;
using Loom.Core.Materials;

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

	var settings = new WebGpuEngineSettings { EnableValidation = true };
	var backend = Engine.AvailableEngines.First(e => e.Name == "WebGPU");
	// Render at the canvas's real display resolution (CSS size × devicePixelRatio) so the output fills the window
	// crisply — the engine's size, the canvas backing store, and the viewport all agree.
	var (width, height) = ParseSize(Host.GetCanvasSize("#loom-canvas"));
	var engine = backend.Create(surface, width, height, settings);

	// Phase 2 proof: a graph-driven fullscreen technique. A "scene" target is cleared to red, then the engine's
	// `blit` technique samples it and writes the backbuffer — exercising the whole technique path (WGSL module,
	// bind-group layout derived from the emitted WGSL, render pipeline, sampler, bind group, the fullscreen draw).
	engine.DefineTechnique("blit", new TechniqueDescriptor("blit"));
	var graph = RenderGraph.Define(g =>
	{
		g.BackbufferClear = new Vector4(0.10f, 0.30f, 0.60f, 1.0f);
		var scene = g.Screen.Color("scene", Format.Rgba8Srgb, clearValue: new Vector4(0.85f, 0.15f, 0.15f, 1.0f));
		g.Pass("fill").Writes(scene).Draw(_ => { }); // clear-only: the pass's Writes clears `scene` to red
		var backbuffer = g.Backbuffer;
		g.Pass("blit").Writes(backbuffer).Samples(scene).Draw(ctx => ctx.FullScreen("blit", scene));
		g.Present(backbuffer);
	});
	engine.SetGraph(graph);

	App.Attach(engine);
	Host.SetStatus($"Loom running on {engine.DeviceName} ({width}×{height})");
	Host.StartFrameLoop(App.Frame);
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
	private static double _lastTimestampMs;
	private static float _time;

	public static void Attach(IEngine engine) => _engine = engine;

	private static bool _faulted;

	public static void Frame(double timestampMs)
	{
		if (_engine is null || _faulted)
			return;
		var delta = _lastTimestampMs > 0 ? (timestampMs - _lastTimestampMs) / 1000.0 : 1.0 / 60.0;
		_lastTimestampMs = timestampMs;
		_time += (float)delta;
		try
		{
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
