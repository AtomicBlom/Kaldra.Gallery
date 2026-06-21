using System.Numerics;
using System.Runtime.InteropServices;
using Loom.Core.Geometry;

namespace Loom.Sandbox.Demos;

/// <summary>
/// One batched 2D sprite/tile — the per-instance data a <c>ctx.DrawInstanced(quad, atlas, "sprite", …)</c>
/// reads through <see cref="SandboxVertexLayouts.Sprite"/>. A unit quad is expanded by the vertex shader into the
/// quad this instance describes: placed at <see cref="Position"/> (with <see cref="Depth"/> for draw order), scaled
/// to <see cref="Size"/> about <see cref="Pivot"/>, rotated by <see cref="Rotation"/>, textured from the atlas region
/// <see cref="UvRect"/>, and multiplied by <see cref="Tint"/>. The field order + <see cref="LayoutKind.Sequential"/>
/// pack it into the layout's four float4 rows exactly (64 bytes), so an array of these uploads straight to the
/// instance stream — the 2D analogue of handing a <see cref="Matrix4x4"/> array to <see cref="VertexLayout.Transform"/>.
/// </summary>
/// <param name="Position">World-space centre-of-pivot position (x, y).</param>
/// <param name="Depth">World-space depth / layer along the camera's view axis — larger is farther (2D ordering).</param>
/// <param name="Rotation">Rotation about <see cref="Pivot"/>, in radians (0 for axis-aligned sprites/tiles).</param>
/// <param name="Size">World-space quad size (width, height).</param>
/// <param name="Pivot">Normalised anchor within the quad that <see cref="Position"/>/<see cref="Rotation"/> act about — (0.5, 0.5) is the centre, (0, 0) the bottom-left corner.</param>
/// <param name="UvRect">The atlas sub-image as (u0, v0, u1, v1) in [0,1] texture space — a neutral rect a sprite-sheet helper supplies; animating it over time is sprite-sheet animation.</param>
/// <param name="Tint">Multiplied rgba colour (use (1,1,1,1) for the atlas verbatim).</param>
[StructLayout(LayoutKind.Sequential)]
public readonly record struct SpriteInstance(
	Vector2 Position,
	float Depth,
	float Rotation,
	Vector2 Size,
	Vector2 Pivot,
	Vector4 UvRect,
	Vector4 Tint)
{
	/// <summary>An axis-aligned, centre-pivoted sprite — the common case: position + size + atlas region, white tint.</summary>
	public static SpriteInstance At(Vector2 position, Vector2 size, Vector4 uvRect, float depth = 0f) =>
		new(position, depth, 0f, size, new Vector2(0.5f, 0.5f), uvRect, Vector4.One);
}
