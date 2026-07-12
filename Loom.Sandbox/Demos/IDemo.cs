using System.Numerics;

using Loom.Concepts;
using Loom.Core.Graph;
using Loom.Core.Hosting;
using SUIsei;
using SUIsei.Input;

namespace Loom.Sandbox.Demos;

/// <summary>
/// One self-contained showcase of a single engine capability. Each demo lives in its own file and is meant to
/// be read top-to-bottom to see how that feature is expressed against the engine: it declares the techniques
/// (shaders) it draws with and builds the render graph for it. The launcher (<c>Program</c>) registers every
/// demo's techniques once, builds each graph, and swaps the active one at runtime (engine.SetGraph) — the demos
/// themselves stay free of any host/loop concerns.
/// </summary>
internal interface IDemo
{
	/// <summary>Display name (menu + status + the console list).</summary>
	string Name { get; }

	/// <summary>Register the techniques this demo draws with. Called once at startup; idempotent across demos that share a technique.</summary>
	void DefineTechniques(IEngine engine);

	/// <summary>
	/// Optional async asset preload, called once before <see cref="Build"/>. A demo that loads textures/models
	/// fetches its bytes (<see cref="DemoScene.Asset"/>), decodes, and uploads (<see cref="IEngine.CreateTextureAsync"/>)
	/// here — concurrently, across as many assets as it needs — and stashes the resulting handles into its own
	/// fields for <see cref="Build"/> to read. Default no-op, so a demo with nothing to load needs no override.
	/// </summary>
	Task PreloadAsync(DemoScene scene) => Task.CompletedTask;

	/// <summary>Build this demo's render graph. <paramref name="scene"/> carries the shared meshes the host uploaded.
	/// Called after <see cref="PreloadAsync"/> has completed, so any fields it populated are ready to read.</summary>
	RenderGraph Build(DemoScene scene);

	/// <summary>
	/// Optional per-frame CPU update with the frame's <see cref="Camera"/>, called by the launcher before
	/// <c>Render</c>. This is where a demo publishes its view-projection(s) into the <c>g.Constants</c> buffers its
	/// passes read (<c>engine.Write(handle, camera.ViewProjection)</c>), fits its shadow cascades, and does any
	/// app-side work like CPU frustum culling — all before the pass closures run. Default no-op.
	/// </summary>
	void Update(Camera camera) { }

	/// <summary>
	/// Optional camera override for this demo, given the frame's framebuffer <paramref name="aspect"/> and elapsed
	/// <paramref name="time"/>. A 2D/sprite demo returns an orthographic <see cref="Camera"/> here and the launcher
	/// renders from it instead of the default perspective orbit; default null keeps the orbit camera. Pulled each
	/// frame, not captured (so a 2D camera can scroll), exactly like the other per-frame inputs.
	/// </summary>
	Camera? GetCamera(float aspect, float time) => null;

	/// <summary>
	/// Optional world-space debug line segments for this frame — pairs of endpoints the launcher projects
	/// to screen and feeds the ImGui overlay painter's <c>SetDebugLines</c>. The skinning demo returns its skeleton
	/// (bone segments + joint axis gizmos) here. Given the frame's effect <paramref name="time"/> so a demo can
	/// resample its animation deterministically. Default none.
	/// </summary>
	IReadOnlyList<(System.Numerics.Vector3 A, System.Numerics.Vector3 B)> DebugWorldLines(float time) => [];

	/// <summary>
	/// Optional debug controls for this demo (cascade-tint, time-of-day scrubber, skeleton overlay,
	/// bind-vs-animated) — a SUIsei element the shared demo shell hosts in its debug panel while this demo is
	/// active. Built once and reused across frames; a demo backing it with an observable view-model (e.g.
	/// <c>CommunityToolkit.Mvvm</c>'s <c>[ObservableProperty]</c>) keeps the panel's controls and the demo's own
	/// state in sync without any host-side wiring. Default none.
	/// </summary>
	UIElement? DebugPanel => null;

	/// <summary>Optional one-line status the launcher appends to the HUD (e.g. instancing's drawn/culled counts). Default none.</summary>
	string? HudStatus => null;

	/// <summary>
	/// Optional pointer hook — called once per raw screen-space pointer event (move, press, release), before the
	/// frame camera is computed. A demo that owns an in-world panel queues the event here for later projection in
	/// <see cref="PreFrame"/>. Default no-op.
	/// </summary>
	void OnPointer(in PointerInput e) { }

	/// <summary>
	/// Optional pre-render step with this frame's render camera and framebuffer size, called after
	/// <see cref="Update"/> but before <c>engine.Render</c>. A demo that owns an in-world panel ticks and
	/// composites it here (project queued screen-pointer hits, drive layout, render draw list). Default no-op.
	/// </summary>
	void PreFrame(double deltaSeconds, float time, Camera camera, Vector2 framebufferSize) { }

	/// <summary>
	/// The object-id buffer this demo writes for cursor picking, or null if it doesn't pick. The
	/// launcher reads it back at the cursor (<see cref="IEngine.Read"/>), decodes the hovered id, and hands it to
	/// <see cref="WriteHover"/> — picking is app-owned, so the engine carries no picking concept of its own.
	/// </summary>
	ResourceHandle? PickTarget => null;

	/// <summary>Write the hovered object id into the demo's outline parameter buffer (<see cref="IEngine.Write{T}(ConstantsHandle, in T)"/>). No-op unless the demo picks.</summary>
	void WriteHover(IEngine engine, uint hoveredId) { }
}
