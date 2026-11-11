using System.Runtime.InteropServices;

using Loom.Assets;
using Loom.Core.Geometry;
using Loom.Core.Graph;
using Loom.Core.Hosting;
using Loom.Core.Materials;
using Loom.Gallery.Demos;
using Loom.Input;
using Loom.UI.SUIsei;

using SUIsei;

namespace Loom.Gallery.Hosting;

/// <summary>
/// The demo gallery as a <see cref="LoomApplication"/> with nothing desktop-only in it: the shared geometry and brick
/// material, the <see cref="DemoGallery"/> roster with each demo loaded the first time it is shown, and the SUIsei
/// <see cref="DemoShell"/> to switch between them by pointer or touch. The desktop sandbox adds its ImGui dev tools,
/// hotkeys and free-fly camera on top of the same gallery; this is what a host without those runs, such as a phone.
/// </summary>
/// <remarks>
/// A host without a folder beside the app (an APK) hands in <c>readAsset</c>, which returns the bytes of a file under
/// <c>Loom.Gallery/Assets</c> by its path there. Escape, which Back is on Android, leaves the app.
/// </remarks>
internal sealed class GalleryApp(Func<string, byte[]> readAsset) : LoomApplication
{
	private bool _ready;
	private UIHost _uiHost = null!;
	private UIView _overlay = null!;
	private DemoShell _shell = null!;
	private DemoScene _scene;
	private IReadOnlyList<IDemo> _demos = [];
	private Loom.Concepts.Camera _orbit = null!;

	// Each demo is loaded the first time it is shown; the task is kept, so asking again while it loads waits on the same
	// load rather than starting another.
	private RenderGraph?[] _graphs = [];
	private Task<RenderGraph>?[] _loadTasks = [];
	private int _currentDemo;
	private int _pendingDemo;

	private bool _pauseCamera;
	private double _elapsed;
	private float _fpsTimer;
	private int _fpsFrames;
	private double _displayFps;
	private double _displayMs;

	protected override void Configure(AppOptions options)
	{
		options.Title = "Loom - demo gallery";
		options.Width = 1280;
		options.Height = 720;
	}

	protected override void LoadContent()
	{
		var engine = Engine;
		var (cubeVerts, cubeIndices) = Geometry.BuildCube();
		var (planeVerts, planeIndices) = Geometry.BuildPlane();
		var (quadVerts, quadIndices) = Geometry.BuildQuad();
		var brick = engine.CreateMaterial(
			engine.CreateTexture(ImageLoader.Decode(readAsset("brick_albedo.png"), TextureFormat.Rgba8Srgb)),
			engine.CreateTexture(ImageLoader.Decode(readAsset("brick_normal.png"), TextureFormat.Rgba8Unorm)));

		_uiHost = new UIHost();
		var uiPainter = UIPainter.Create(engine, _uiHost);
		_scene = new DemoScene(engine.CreateMesh(cubeVerts, cubeIndices), engine.CreateMesh(planeVerts, planeIndices),
			engine.CreateMesh(quadVerts, quadIndices), brick, uiPainter, name => Task.Run(() => readAsset(name)));
		_demos = DemoGallery.Build(engine);
		_graphs = new RenderGraph?[_demos.Count];
		_loadTasks = new Task<RenderGraph>?[_demos.Count];
		_orbit = new Loom.Concepts.Camera(engine);

		// The first demo loads before the first frame, blocking until it is ready. Its awaits must not come back to this
		// thread while it waits: where the thread has a synchronization context (Android's UI thread), they would queue
		// behind the wait forever. Without one they continue on the thread pool, as they do on the desktop.
		var context = SynchronizationContext.Current;
		SynchronizationContext.SetSynchronizationContext(null);
		try
		{
			PumpUntilReady(engine, EnsureLoadedAsync(0)).GetAwaiter().GetResult();
		}
		finally
		{
			SynchronizationContext.SetSynchronizationContext(context);
		}

		_shell = new DemoShell([.. _demos.Select(d => d.Name)], SelectDemo);
		_shell.AddHostToggle("Pause camera", () => _pauseCamera, v => _pauseCamera = v);
		_shell.AddHostToggle("GPU timing", () => engine.GpuTimingEnabled, v => engine.GpuTimingEnabled = v);
		_shell.SetHudVisible(true);
		DemoGraph.Overlay = null; // no ImGui here

		_overlay = _uiHost.CreateRoot(UILayer.Default);
		_overlay.Root.Content = _shell.Root;
		engine.SetGraph(_graphs[0]!);

		// Bottom to top: the active demo's in-world panel, the shell and the demos' own pages, then Escape to leave.
		PushInputLayer(new DemoPointerLayer(e => _demos[_currentDemo].OnPointer(e)));
		PushInputLayer(new SUIseiInputLayer(_uiHost));
		PushInputLayer(new ExitOnEscapeLayer(Exit));
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

	protected override void BeforeRender(FrameTime time)
	{
		if (!_ready)
			return;

		var engine = Engine;
		var fbW = (int)SurfaceSize.X;
		var fbH = (int)SurfaceSize.Y;
		var aspect = fbH == 0 ? 1f : (float)fbW / fbH;
		var camTime = (float)_elapsed;
		var demo = _demos[_currentDemo];

		DemoCamera.Orbit(_orbit, camTime, aspect);
		var camera = demo.GetCamera(aspect, camTime) ?? _orbit;

		// A demo with a pick target reads its object ids back under the pointer (the last touch, on a phone).
		var hoverId = 0u;
		if (demo.PickTarget is { } pickTarget)
		{
			var pointer = Input.PointerPosition;
			var (pw, ph) = pickTarget.Resolve();
			var px = (int)(pointer.X / Math.Max(fbW, 1) * pw);
			var py = (int)(pointer.Y / Math.Max(fbH, 1) * ph);
			var pixels = MemoryMarshal.Cast<byte, Half>(engine.Read(pickTarget));
			var i = py * pw + px;
			if ((uint)i < (uint)pixels.Length)
				hoverId = (uint)MathF.Round((float)pixels[i]);
			demo.WriteHover(engine, hoverId);
		}

		_shell.SetActiveDemo(demo.Name, demo.DebugPanel);
		_shell.SetEngineName(engine.DeviceName);
		_shell.SetFps((float)_displayFps, (float)_displayMs);
		_shell.SetStatus($"hovered {hoverId}");
		_shell.SetStatusRight(demo.HudStatus);
		_shell.SetCenterContent(demo.OverlayContent);

		// Read every frame: the scale follows the display, and the keyboard's height comes and goes.
		_overlay.Root.LayoutScale = DisplayScale;
		_overlay.Root.InputPaneHeight = InputPaneHeight;
		_uiHost.Tick(time.DeltaSeconds, camTime, SurfaceSize);
		_uiHost.Render();
		_scene.UI.Prepare();

		// After the UI's tick, where a switch from the shell's menu lands, so the demo drawn this frame is updated.
		demo = _demos[_currentDemo];
		demo.Update(camera);
		demo.PreFrame(time.DeltaSeconds, camTime, camera, new System.Numerics.Vector2(fbW, fbH));
	}

	private Task<RenderGraph> EnsureLoadedAsync(int index) => _loadTasks[index] ??= LoadAsync(index);

	private async Task<RenderGraph> LoadAsync(int index)
	{
		await _demos[index].PreloadAsync(_scene);
		var graph = _demos[index].Build(_scene);
		_graphs[index] = graph;
		return graph;
	}

	// Load (or reuse the load of) the demo, then show it unless a later choice has replaced it while it loaded. The demo
	// shown so far keeps drawing until then.
	private async Task SwitchDemoAsync(int index)
	{
		_pendingDemo = index;
		var graph = await EnsureLoadedAsync(index);
		if (_pendingDemo != index)
			return;
		_currentDemo = index;
		Engine.SetGraph(graph);
	}

	private void SelectDemo(int index)
	{
		if (index < 0 || index >= _demos.Count || index == _currentDemo || index == _pendingDemo)
			return;
		_ = SwitchDemoAsync(index);
	}

	// Run `task` to completion while draining the engine's queued uploads on this thread, which nothing else does before
	// the first frame.
	private static async Task PumpUntilReady(IEngine engine, Task task)
	{
		while (!task.IsCompleted)
		{
			engine.ServicePendingUploads();
			await Task.Yield();
		}
		await task;
	}

	// Escape leaves the app: on Android, the system's Back.
	private sealed class ExitOnEscapeLayer(Action exit) : IInputLayer
	{
		public void Process(ref InputContext ctx)
		{
			foreach (ref readonly var e in ctx.Incoming)
				if (e.Kind == InputEventKind.KeyDown && e.Key == Key.Escape)
					exit();
		}
	}
}
