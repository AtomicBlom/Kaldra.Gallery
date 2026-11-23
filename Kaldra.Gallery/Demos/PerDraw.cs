using System.Numerics;
using System.Runtime.InteropServices;

namespace Kaldra.Gallery.Demos;

/// <summary>
/// The per-draw block the sandbox's mesh shaders read as their per-object constant buffer: a model matrix and a
/// picking id. This is a <em>content</em> contract between the sandbox shaders' <c>struct PerDraw { float4x4 model;
/// uint objectId; }</c> and the data <see cref="DemoDraw"/> feeds them, so it lives with the demos rather than in
/// the engine — most renderers don't tag every draw with an id, so "every draw has a model + id" is the sandbox's
/// opinion, not a neutral engine concept. Sized to 80 bytes to match that cbuffer: a <c>float4x4</c> (64) plus a
/// <c>uint</c> rounds up to a 16-byte boundary, so the typed struct must carry the same trailing padding.
/// </summary>
[StructLayout(LayoutKind.Sequential, Size = 80)]
internal struct PerDraw
{
	/// <summary>The object's model-to-world matrix (row-major; the cbuffer packs its transpose, so the shader applies it column-form).</summary>
	public Matrix4x4 Model;

	/// <summary>The object's picking id (written to the id target by the id technique); 0 = untagged.</summary>
	public uint ObjectId;
}
