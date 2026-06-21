using System.Numerics;
using Loom.Core.Geometry;

namespace Loom.Sandbox.Demos;

/// <summary>The meshes every demo shares — a unit cube, a ground plane, and a unit quad — built once by the host.</summary>
internal static class Geometry
{
	// A unit quad in the XY plane, corners (0,0)..(1,1), facing +Z with a +X tangent (a TBN frame for normal
	// mapping). It carries the standard Vertex layout so it uploads through the ordinary CreateMesh and instances
	// with SandboxVertexLayouts.Sprite (M14): the sprite vertex shader reads only POSITION + TEXCOORD and ignores the
	// normal/tangent, expanding each corner into the quad the per-instance SpriteInstance describes. UV = corner,
	// so (0,0) is the quad's bottom-left; the shader applies the atlas uv-rect (and the v-flip) per sprite.
	public static (Vertex[] Vertices, uint[] Indices) BuildQuad()
	{
		var n = new Vector3(0, 0, 1);
		var tan = new Vector4(1, 0, 0, 1);
		Vertex[] vertices =
		[
			new(new Vector3(0, 0, 0), n, new Vector2(0, 0), tan),
			new(new Vector3(1, 0, 0), n, new Vector2(1, 0), tan),
			new(new Vector3(1, 1, 0), n, new Vector2(1, 1), tan),
			new(new Vector3(0, 1, 0), n, new Vector2(0, 1), tan),
		];
		return (vertices, [0, 1, 2, 0, 2, 3]);
	}

	// A flat ground quad at the cube's base (y = -0.5), facing up — the receiver the cube's shadow falls on.
	// Wound counter-clockwise viewed from above so the up-facing side is the front face (matching the cube and the
	// glTF convention), which keeps it visible under back-face culling; the normal points up for lighting.
	public static (Vertex[] Vertices, uint[] Indices) BuildPlane()
	{
		const float s = 4f;
		const float y = -0.5f;
		var up = new Vector3(0, 1, 0);
		Vector3 a = new(-s, y, -s), b = new(s, y, -s), c = new(s, y, s), d = new(-s, y, s);
		// One tangent for the whole flat quad, derived from its corner UVs so normal mapping has a TBN frame.
		var tan = Tangent(up, a, b, c, new(0, 0), new(1, 0), new(1, 1));
		Vertex[] vertices =
		[
			new(a, up, new(0, 0), tan),
			new(b, up, new(1, 0), tan),
			new(c, up, new(1, 1), tan),
			new(d, up, new(0, 1), tan),
		];
		return (vertices, [0, 2, 1, 0, 3, 2]);
	}

	// A unit cube centred at the origin, 24 vertices (4 per face) so each face carries its own flat normal,
	// 36 indices (two triangles per face). The lit/mesh techniques colour by normal, so each face reads distinctly.
	public static (Vertex[] Vertices, uint[] Indices) BuildCube()
	{
		// (normal, and the four corners of that face in CCW order viewed from outside)
		(Vector3 Normal, Vector3 A, Vector3 B, Vector3 C, Vector3 D)[] faces =
		[
			(new(0, 0, 1), new(-0.5f, -0.5f, 0.5f), new(0.5f, -0.5f, 0.5f), new(0.5f, 0.5f, 0.5f), new(-0.5f, 0.5f, 0.5f)),     // +Z
			(new(0, 0, -1), new(0.5f, -0.5f, -0.5f), new(-0.5f, -0.5f, -0.5f), new(-0.5f, 0.5f, -0.5f), new(0.5f, 0.5f, -0.5f)), // -Z
			(new(1, 0, 0), new(0.5f, -0.5f, 0.5f), new(0.5f, -0.5f, -0.5f), new(0.5f, 0.5f, -0.5f), new(0.5f, 0.5f, 0.5f)),      // +X
			(new(-1, 0, 0), new(-0.5f, -0.5f, -0.5f), new(-0.5f, -0.5f, 0.5f), new(-0.5f, 0.5f, 0.5f), new(-0.5f, 0.5f, -0.5f)), // -X
			(new(0, 1, 0), new(-0.5f, 0.5f, 0.5f), new(0.5f, 0.5f, 0.5f), new(0.5f, 0.5f, -0.5f), new(-0.5f, 0.5f, -0.5f)),      // +Y
			(new(0, -1, 0), new(-0.5f, -0.5f, -0.5f), new(0.5f, -0.5f, -0.5f), new(0.5f, -0.5f, 0.5f), new(-0.5f, -0.5f, 0.5f)), // -Y
		];

		var vertices = new List<Vertex>(24);
		var indices = new List<uint>(36);
		foreach (var (normal, a, b, c, d) in faces)
		{
			var v = (uint)vertices.Count;
			// One tangent per flat face, derived from the face's corner UVs (A=(0,0)..D=(0,1)).
			var tan = Tangent(normal, a, b, c, new(0, 0), new(1, 0), new(1, 1));
			vertices.Add(new Vertex(a, normal, new Vector2(0, 0), tan));
			vertices.Add(new Vertex(b, normal, new Vector2(1, 0), tan));
			vertices.Add(new Vertex(c, normal, new Vector2(1, 1), tan));
			vertices.Add(new Vertex(d, normal, new Vector2(0, 1), tan));
			indices.AddRange([v, v + 1, v + 2, v, v + 2, v + 3]);
		}

		return ([.. vertices], [.. indices]);
	}

	// The glTF-convention tangent for a triangle: xyz along +U, w = ±1 bitangent handedness, so a shader can
	// rebuild bitangent = cross(normal, tangent.xyz) * w. Gram-Schmidt orthonormalises against the normal.
	private static Vector4 Tangent(Vector3 n, Vector3 p0, Vector3 p1, Vector3 p2, Vector2 uv0, Vector2 uv1, Vector2 uv2)
	{
		var e1 = p1 - p0;
		var e2 = p2 - p0;
		var d1 = uv1 - uv0;
		var d2 = uv2 - uv0;
		var denom = d1.X * d2.Y - d2.X * d1.Y;
		var r = denom != 0f ? 1f / denom : 0f;
		var t = (e1 * d2.Y - e2 * d1.Y) * r;
		var bitangent = (e2 * d1.X - e1 * d2.X) * r;
		var ortho = t - n * Vector3.Dot(n, t); // Gram-Schmidt; guard the degenerate-UV case so we never normalize a zero vector
		t = ortho.LengthSquared() > 1e-12f ? Vector3.Normalize(ortho) : new Vector3(1, 0, 0);
		var w = Vector3.Dot(Vector3.Cross(n, t), bitangent) < 0f ? -1f : 1f;
		return new Vector4(t, w);
	}
}
