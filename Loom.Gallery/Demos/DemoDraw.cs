using System.Numerics;

using Loom.Core.Geometry;
using Loom.Core.Graph;
using Loom.Core.Hosting;
using Loom.Core.Materials;

namespace Loom.Gallery.Demos;

/// <summary>
/// Places a sandbox object in a scene pass: packs its model matrix + picking id into the <see cref="PerDraw"/> block
/// and submits the draw. This is the sandbox's "draw a placed object" convention, built on the neutral
/// <see cref="DrawCall.SetUniforms{T}(ref T)"/> seam — the engine carries no model/id opinion, the sandbox does. The
/// pass must <c>.Reads</c> the view's view-projection constants (a <c>Loom.Concepts.Camera.ViewProjection</c>) so the
/// vertex shader has its view-projection. The <see cref="PerDraw"/> lives on the stack and the engine uploads it
/// synchronously inside <see cref="IRenderContext.Draw"/>, so it stays alive for the whole submission with no per-draw
/// allocation.
/// </summary>
internal static class DemoDraw
{
	/// <summary>Draw <paramref name="mesh"/> with <paramref name="technique"/> at <paramref name="model"/>, tagged with <paramref name="objectId"/> (0 = untagged) and an optional <paramref name="material"/>.</summary>
	public static void Mesh(IRenderContext ctx, Mesh mesh, string technique, in Matrix4x4 model, uint objectId = 0, Material? material = null)
	{
		var block = new PerDraw { Model = model, ObjectId = objectId };
		var call = new DrawCall { Mesh = mesh, Technique = technique, Material = material };
		call.SetUniforms(ref block);
		ctx.Draw(call);
	}

	/// <summary>
	/// Draw a compute-pre-skinned <paramref name="mesh"/> (its vertices supplied by <paramref name="skinnedVertices"/>,
	/// a <c>.ReadsVertex</c> buffer) with the same per-draw model + id block. The skinned vertices are already in
	/// object space, so <paramref name="model"/> places that object in the world.
	/// </summary>
	public static void Skinned(IRenderContext ctx, Mesh mesh, BufferHandle skinnedVertices, string technique, in Matrix4x4 model, uint objectId = 0)
	{
		var block = new PerDraw { Model = model, ObjectId = objectId };
		var call = new DrawCall { Mesh = mesh, SkinnedVertices = skinnedVertices, Technique = technique };
		call.SetUniforms(ref block);
		ctx.Draw(call);
	}
}
