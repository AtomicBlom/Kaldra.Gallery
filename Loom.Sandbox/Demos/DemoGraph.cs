using System.Numerics;
using System.Runtime.InteropServices;
using Loom.Core;

namespace Loom.Sandbox.Demos;

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