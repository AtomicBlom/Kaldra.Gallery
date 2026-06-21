using System.Numerics;
using System.Runtime.InteropServices;
using Loom.Core.Graph;
using Loom.UI.ImGui;
using Loom.UI.SUIsei;
using SUIsei.Drawing;

namespace Loom.Sandbox.Demos;

/// <summary>Small helpers shared by the demo graphs — the native-resolution ImGui overlay and a matrix→push view.</summary>
internal static class DemoGraph
{
	/// <summary>
	/// The Dear ImGui overlay painter (the optional <c>Loom.UI.ImGui</c> bridge), set once by the host. The ImGui
	/// context is process-global, so a static handle mirrors that — unlike the per-host SUIsei painter. Null leaves
	/// the HUD pass a no-op (ImGui not in use), so every demo still renders.
	/// </summary>
	public static ImGuiPainter? Overlay { get; set; }

	/// <summary>
	/// Append the Dear ImGui overlay (§6/§9): a pass that <c>.Blends</c> the backbuffer (so the engine orders it
	/// after the present write and derives the barrier) and submits this frame's ImGui draw data through the public
	/// draw-list API via <see cref="Overlay"/>. Every demo ends with this so the menu bar / Demo switcher / status
	/// render on all of them. A no-op when no painter is set.
	/// </summary>
	public static void Hud(GraphBuilder g, ColorHandle screen) =>
		g.Pass("hud").Blends(screen).Draw(ctx => Overlay?.Draw(ctx));

	/// <summary>
	/// Append the game-UI overlay: a pass that <c>.Blends</c> the backbuffer at native resolution and submits the
	/// named draw list through <paramref name="painter"/> (which renders it via the public draw-list API) — the twin
	/// of <see cref="Hud"/> for the XAML UI engine. Place before <see cref="Hud"/> so the debug HUD stays on top.
	/// A no-op when the frame carries no matching list in <see cref="FrameInputs.DrawLists"/>.
	/// </summary>
	public static void UI(GraphBuilder g, ColorHandle screen, UIPainter painter, DrawListId surface) =>
		g.Pass("ui").Blends(screen).Draw(ctx => painter.Draw(ctx, surface));

	/// <summary>
	/// View a matrix as its 16 contiguous floats (System.Numerics field order — exactly what a shader's
	/// <c>float4x4</c> push expects) with no per-frame allocation. The span aliases the caller's matrix
	/// temporary, which lives to the end of the enclosing push call where it is read synchronously.
	/// </summary>
	public static ReadOnlySpan<float> Mat(in Matrix4x4 m) => MemoryMarshal.CreateReadOnlySpan(in m.M11, 16);
}
