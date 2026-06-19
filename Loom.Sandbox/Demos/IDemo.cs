using System.Numerics;
using System.Runtime.InteropServices;

using Loom.Core;

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

	/// <summary>Optional one-line status the launcher appends to the HUD (e.g. instancing's drawn/culled counts). Default none.</summary>
	string? HudStatus => null;
}

/// <summary>The shared, app-owned geometry every demo draws — uploaded once by the host and handed to each demo.
/// <paramref name="NormalMapped"/> is a brick material (albedo + normal map) the M11 demos draw with.</summary>
internal readonly record struct DemoScene(Mesh Cube, Mesh Plane, Material NormalMapped);

/// <summary>Small helpers shared by the demo graphs — the native-resolution ImGui overlay and a matrix→push view.</summary>
internal static class DemoGraph
{
	/// <summary>
	/// Append the Dear ImGui overlay (§6/§9): a pass that <c>.Blends</c> the backbuffer (so the engine orders it
	/// after the present write and derives the barrier) and hands this frame's draw data to the backend renderer.
	/// Every demo ends with this so the menu bar / Demo switcher / status render on all of them. A no-op when
	/// ImGui isn't enabled.
	/// </summary>
	public static void Hud(GraphBuilder g, ColorHandle screen) =>
		g.Pass("hud").Blends(screen).Draw(ctx => ctx.DrawImGui());

	/// <summary>
	/// Append the game-UI overlay (PlanningUI.md §D4.1): a pass that <c>.Blends</c> the backbuffer at native
	/// resolution and hands the named surface's composited draw data to the backend's UI renderer — the twin of
	/// <see cref="Hud"/> for the XAML UI engine. Place before <see cref="Hud"/> so the debug HUD stays on top.
	/// A no-op until <c>engine.EnableUI()</c> and a matching surface in <see cref="FrameInputs.UiSurfaces"/>;
	/// in MU0 Phase A the backend renderer is a no-op, so this proves the seam runs clean on both backends.
	/// </summary>
	public static void Ui(GraphBuilder g, ColorHandle screen, UiSurfaceId surface) =>
		g.Pass("ui").Blends(screen).Draw(ctx => ctx.DrawUI(surface));

	/// <summary>
	/// View a matrix as its 16 contiguous floats (System.Numerics field order — exactly what a shader's
	/// <c>float4x4</c> push expects) with no per-frame allocation. The span aliases the caller's matrix
	/// temporary, which lives to the end of the enclosing push call where it is read synchronously.
	/// </summary>
	public static ReadOnlySpan<float> Mat(in Matrix4x4 m) => MemoryMarshal.CreateReadOnlySpan(in m.M11, 16);
}
