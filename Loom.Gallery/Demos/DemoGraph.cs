using System.Numerics;
using System.Runtime.InteropServices;
using Loom.Core.Graph;
#if !LOOM_WEB
using Loom.UI.ImGui;
#endif
using Loom.UI.SUIsei;

namespace Loom.Gallery.Demos;

/// <summary>Small helpers shared by the demo graphs — the native-resolution ImGui overlay and a matrix→push view.</summary>
internal static class DemoGraph
{
#if !LOOM_WEB
	/// <summary>
	/// The Dear ImGui overlay painter (the optional <c>Loom.UI.ImGui</c> bridge), set once by the host. The ImGui
	/// context is process-global, so a static handle mirrors that — unlike the per-host SUIsei painter. Null leaves
	/// the HUD pass a no-op (ImGui not in use), so every demo still renders.
	/// </summary>
	public static ImGuiPainter? Overlay { get; set; }
#endif

	/// <summary>
	/// Append the shell overlay: a pass that <c>.Blends</c> the backbuffer (so the engine orders it after the present
	/// write and derives the barrier) and submits the shared SUIsei demo shell (<see cref="DemoScene.UI"/>'s
	/// <see cref="SUIsei.UISurface.Hud"/> draw list — the menu bar, debug panel, status bar, and, when the active demo
	/// hosts one, its own centred UI content) through the public draw-list API, then — desktop-only — this frame's
	/// Dear ImGui draw data on top for the dev-tools window. Every demo ends with this so the shell renders on all of
	/// them identically; entirely ImGui-free on the browser build, which has no Dear ImGui bridge.
	/// </summary>
	public static void Hud(GraphBuilder g, ColorHandle screen, DemoScene scene) =>
		g.Pass("hud").Blends(screen).Draw(ctx =>
		{
			scene.UI.Draw(ctx, SUIsei.UISurface.Hud);
#if !LOOM_WEB
			Overlay?.Draw(ctx);
#endif
		});

	/// <summary>
	/// View a matrix as its 16 contiguous floats (System.Numerics field order — exactly what a shader's
	/// <c>float4x4</c> push expects) with no per-frame allocation. The span aliases the caller's matrix
	/// temporary, which lives to the end of the enclosing push call where it is read synchronously.
	/// </summary>
	public static ReadOnlySpan<float> Mat(in Matrix4x4 m) => MemoryMarshal.CreateReadOnlySpan(in m.M11, 16);

	/// <summary>
	/// The outline seed pass's parameters — the hovered object id (0 = nothing) in <c>P.X</c>. The host writes it
	/// each frame from the picking readback (<c>IEngine.Read</c>) via the demo's <c>WriteHover</c>, and the seed
	/// technique reads it from a <c>g.Constants</c> buffer it <c>.Reads</c>. A single float4 row — identical
	/// std140/cbuffer layout on both backends.
	/// </summary>
	public readonly record struct OutlineParams(Vector4 P);

	/// <summary>
	/// The cascaded-shadow split parameters a CSM lighting pass <c>.Reads</c>: the three cascades' far view-depths
	/// in <c>P.X/Y/Z</c> and a cascade-tint debug flag in <c>P.W</c>. The host writes it from its fitted cascades
	/// (<c>IEngine.Write</c>); the lighting shader reads it from uniform slot 0, leaving the clip-space cascade
	/// matrices on the in-closure push (slot 1) where the engine's per-cascade Y-flip can apply. A single float4 row.
	/// </summary>
	public readonly record struct CsmSplits(Vector4 P);
}
