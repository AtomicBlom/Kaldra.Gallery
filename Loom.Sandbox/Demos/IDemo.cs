using Loom.Core;
using Loom.Core.Geometry;
using Loom.Core.Graph;
using Loom.Core.Hosting;

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

	/// <summary>Build this demo's render graph. <paramref name="scene"/> carries the shared meshes the host uploaded.</summary>
	RenderGraph Build(DemoScene scene);

	/// <summary>
	/// Optional per-frame CPU update with the frame's camera, called by the launcher before <c>Render</c> — the
	/// hook a demo uses for app-side work like CPU frustum culling (M12), where the surviving draw list must be
	/// ready before the pass closure runs. Default no-op; only the instancing demo overrides it.
	/// </summary>
	void Update(Camera camera) { }

	/// <summary>
	/// Optional camera override for this demo, given the frame's framebuffer <paramref name="aspect"/> and elapsed
	/// <paramref name="time"/>. A 2D/sprite demo (M14) returns an orthographic view here and the launcher renders
	/// from it instead of the default perspective orbit; default null keeps the orbit camera. Pulled each frame,
	/// not captured (so a 2D camera can scroll), exactly like the other per-frame inputs.
	/// </summary>
	Camera? GetCamera(float aspect, float time) => null;

	/// <summary>
	/// Optional cascaded-shadow-map views for this frame (M15-3). A demo that wants CSM fits N cascades to its own
	/// camera frustum + the sun <paramref name="lightDir"/> (it knows its camera's near/far) and returns the
	/// per-cascade ortho cameras + their far view-depths; the launcher threads them into <c>FrameInputs.Cascades</c>/
	/// <c>CascadeSplits</c> so the depth passes and deferred lighting can pull them. Default null — the demo uses
	/// the single <c>SunView</c> shadow (or none). Pulled each frame, not captured, like <see cref="GetCamera"/>.
	/// </summary>
	(IReadOnlyList<Camera> Cascades, IReadOnlyList<float> Splits)? GetCascades(Camera camera, System.Numerics.Vector3 lightDir) => null;

	/// <summary>
	/// Optional world-space debug line segments for this frame (M15-7) — pairs of endpoints the launcher projects
	/// to screen and feeds the ImGui overlay painter's <c>SetDebugLines</c>. The skinning demo returns its skeleton
	/// (bone segments + joint axis gizmos) here. Given the frame's effect <paramref name="time"/> so a demo can
	/// resample its animation deterministically. Default none.
	/// </summary>
	IReadOnlyList<(System.Numerics.Vector3 A, System.Numerics.Vector3 B)> DebugWorldLines(float time) => [];

	/// <summary>
	/// Optional Dear ImGui controls for this demo (M15-7) — drawn by the launcher inside a "Debug" menu/window so a
	/// demo can expose its own toggles (cascade-tint, time-of-day scrubber, skeleton overlay, bind-vs-animated).
	/// Default no-op. Only meaningful when ImGui is enabled.
	/// </summary>
	void DrawDebugUI() { }

	/// <summary>Optional one-line status the launcher appends to the HUD (e.g. instancing's drawn/culled counts). Default none.</summary>
	string? HudStatus => null;
}
