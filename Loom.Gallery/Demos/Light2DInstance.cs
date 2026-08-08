using System.Numerics;
using System.Runtime.InteropServices;
using Loom.Core.Geometry;

namespace Loom.Gallery.Demos;

/// <summary>
/// One 2D point light — the per-instance data a <c>ctx.Draw</c> with a <c>DrawCall.Instances</c> source
/// reads through <see cref="SandboxVertexLayouts.Light2D"/>. Each light is one additive quad sized to its reach: the
/// vertex shader expands a unit quad to the light's bounding box (<see cref="Position"/> ± <see cref="Radius"/>),
/// and the fragment samples the 2D G-buffer normal under each covered pixel and accumulates
/// <c>colour · intensity · N·L · attenuation</c> additively into the lit target. A whole field of dynamic,
/// unbounded-count lights is one instanced draw — overdraw only where the lights actually reach.
/// <para>
/// <see cref="Height"/> lifts the light off the 2D plane (a notional Z above the sprites): the light direction
/// gains a Z component, so flat-facing pixels (normal +Z) still light and the normal-mapped relief reads as 3D.
/// Attenuation uses the in-plane distance (the 2D <see cref="Radius"/> reach); N·L uses the full 3D direction.
/// The field order + <see cref="LayoutKind.Sequential"/> pack it into the layout's two float4 rows exactly (32
/// bytes), so an array uploads straight to the instance stream — the light analogue of <see cref="SpriteInstance"/>.
/// </para>
/// </summary>
/// <param name="Position">Light centre in world space (x, y) — the same world the sprites live in.</param>
/// <param name="Radius">In-plane reach: attenuation falls to zero at this distance (and sizes the light's quad).</param>
/// <param name="Intensity">Brightness multiplier applied to <see cref="Color"/> (HDR — values &gt;1 feed bloom).</param>
/// <param name="Color">Light colour (linear rgb).</param>
/// <param name="Height">Notional Z height above the 2D plane — gives the light direction a Z so normal-mapped relief lights.</param>
[StructLayout(LayoutKind.Sequential)]
public readonly record struct Light2DInstance(
	Vector2 Position,
	float Radius,
	float Intensity,
	Vector3 Color,
	float Height)
{
	/// <summary>A point light at <paramref name="position"/> with the common defaults (a moderate height off the plane).</summary>
	public static Light2DInstance At(Vector2 position, float radius, Vector3 color, float intensity = 1f, float height = 40f) =>
		new(position, radius, intensity, color, height);
}
