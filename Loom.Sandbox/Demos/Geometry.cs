using System.Numerics;
using Loom.Core.Geometry;

namespace Loom.Sandbox.Demos;

/// <summary>The meshes every demo shares — a unit cube, a ground plane, and a unit quad — built once by the host.</summary>
internal static class Geometry
{
	// A unit quad in the XY plane, corners (0,0)..(1,1), facing +Z with a +X tangent (a TBN frame for normal
	// mapping). It carries the standard Vertex layout so it uploads through the ordinary CreateMesh and instances
	// with SandboxVertexLayouts.Sprite: the sprite vertex shader reads only POSITION + TEXCOORD and ignores the
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

	// A UV sphere of unit diameter (radius 0.5) centred at the origin: stacks latitude bands × slices longitude
	// columns. Normals are the normalised position; the tangent is the eastward ∂pos/∂θ orthonormalised against the
	// normal. The poles are a degenerate row of coincident positions (the usual UV-sphere pinching).
	public static (Vertex[] Vertices, uint[] Indices) BuildSphere(int stacks = 16, int slices = 32)
	{
		const float r = 0.5f;
		var vertices = new List<Vertex>((stacks + 1) * (slices + 1));
		var indices = new List<uint>(stacks * slices * 6);
		for (var i = 0; i <= stacks; i++)
		{
			var v = i / (float)stacks;
			var phi = v * MathF.PI;
			var (sp, cp) = (MathF.Sin(phi), MathF.Cos(phi));
			for (var j = 0; j <= slices; j++)
			{
				var u = j / (float)slices;
				var theta = u * MathF.Tau;
				var (st, ct) = (MathF.Sin(theta), MathF.Cos(theta));
				var n = new Vector3(sp * ct, cp, sp * st);
				vertices.Add(new Vertex(n * r, n, new Vector2(u, v), MakeTangent(n, new Vector3(-st, 0, ct))));
			}
		}
		var stride = (uint)(slices + 1);
		for (uint i = 0; i < stacks; i++)
			for (uint j = 0; j < slices; j++)
			{
				var a = i * stride + j;
				var b = a + stride;
				indices.AddRange([a, b, a + 1, a + 1, b, b + 1]);
			}
		return ([.. vertices], [.. indices]);
	}

	// A unit-diameter dome — the top half of a sphere (radius 0.5): a flat underside at y=0 rising to a domed crown
	// at y=0.5, with the underside closed by a disc. The outward dome normals make a glow read brightest on the
	// crown (a mushroom cap); the underside disc faces down (dim gills).
	public static (Vertex[] Vertices, uint[] Indices) BuildHemisphere(int stacks = 8, int slices = 24)
	{
		const float r = 0.5f;
		var vertices = new List<Vertex>();
		var indices = new List<uint>();

		// Dome: stacks from the crown (theta 0) down to the equator (theta π/2).
		for (var i = 0; i <= stacks; i++)
		{
			var theta = i / (float)stacks * (MathF.PI / 2f);
			var (st, ct) = (MathF.Sin(theta), MathF.Cos(theta));
			for (var j = 0; j <= slices; j++)
			{
				var u = j / (float)slices;
				var ang = u * MathF.Tau;
				var (sa, ca) = (MathF.Sin(ang), MathF.Cos(ang));
				var n = new Vector3(st * ca, ct, st * sa);
				vertices.Add(new Vertex(n * r, n, new Vector2(u, ct), MakeTangent(n, new Vector3(-sa, 0, ca))));
			}
		}
		var stride = (uint)(slices + 1);
		for (uint i = 0; i < stacks; i++)
			for (uint j = 0; j < slices; j++)
			{
				var a = i * stride + j;
				var b = a + stride;
				indices.AddRange([a, b, a + 1, a + 1, b, b + 1]);
			}

		// Flat underside disc (closes the dome) at y = 0, facing down.
		var down = -Vector3.UnitY;
		var center = (uint)vertices.Count;
		vertices.Add(new Vertex(Vector3.Zero, down, new Vector2(0.5f, 0.5f), MakeTangent(down, Vector3.UnitX)));
		var rim = (uint)vertices.Count;
		for (var j = 0; j <= slices; j++)
		{
			var ang = j / (float)slices * MathF.Tau;
			var (sa, ca) = (MathF.Sin(ang), MathF.Cos(ang));
			vertices.Add(new Vertex(new Vector3(ca * r, 0f, sa * r), down, new Vector2(ca * 0.5f + 0.5f, sa * 0.5f + 0.5f), MakeTangent(down, Vector3.UnitX)));
		}
		for (uint j = 0; j < slices; j++)
			indices.AddRange([center, rim + j + 1, rim + j]);

		return ([.. vertices], [.. indices]);
	}

	// An icosphere of unit diameter: an icosahedron midpoint-subdivided `subdivisions` times then projected to the
	// sphere, so the triangles stay near-equilateral with no polar pinching. Normals are the normalised position;
	// UVs are a spherical projection (with a harmless longitude seam, since these shapes aren't texture-mapped).
	public static (Vertex[] Vertices, uint[] Indices) BuildIcosphere(int subdivisions = 3)
	{
		const float r = 0.5f;
		var t = (1f + MathF.Sqrt(5f)) / 2f;
		var positions = new List<Vector3>
		{
			new(-1, t, 0), new(1, t, 0), new(-1, -t, 0), new(1, -t, 0),
			new(0, -1, t), new(0, 1, t), new(0, -1, -t), new(0, 1, -t),
			new(t, 0, -1), new(t, 0, 1), new(-t, 0, -1), new(-t, 0, 1),
		};
		for (var i = 0; i < positions.Count; i++)
			positions[i] = Vector3.Normalize(positions[i]);
		var tris = new List<uint>(
		[
			0, 11, 5, 0, 5, 1, 0, 1, 7, 0, 7, 10, 0, 10, 11,
			1, 5, 9, 5, 11, 4, 11, 10, 2, 10, 7, 6, 7, 1, 8,
			3, 9, 4, 3, 4, 2, 3, 2, 6, 3, 6, 8, 3, 8, 9,
			4, 9, 5, 2, 4, 11, 6, 2, 10, 8, 6, 7, 9, 8, 1,
		]);
		for (var s = 0; s < subdivisions; s++)
		{
			var midpoints = new Dictionary<long, uint>();
			var next = new List<uint>(tris.Count * 4);
			for (var f = 0; f < tris.Count; f += 3)
			{
				uint a = tris[f], b = tris[f + 1], c = tris[f + 2];
				var ab = Midpoint(a, b, positions, midpoints);
				var bc = Midpoint(b, c, positions, midpoints);
				var ca = Midpoint(c, a, positions, midpoints);
				next.AddRange([a, ab, ca, b, bc, ab, c, ca, bc, ab, bc, ca]);
			}
			tris = next;
		}
		var vertices = new Vertex[positions.Count];
		for (var i = 0; i < positions.Count; i++)
		{
			var n = positions[i];
			var uv = new Vector2(0.5f + MathF.Atan2(n.Z, n.X) / MathF.Tau, 0.5f - MathF.Asin(Math.Clamp(n.Y, -1f, 1f)) / MathF.PI);
			var du = new Vector3(-n.Z, 0, n.X);
			vertices[i] = new Vertex(n * r, n, uv, MakeTangent(n, du.LengthSquared() > 1e-8f ? du : Vector3.UnitX));
		}
		return (vertices, [.. tris]);
	}

	// A cylinder of `height` along Y (centred at the origin), `radius` wide, `segments` around: a side strip with
	// outward radial normals plus flat top (+Y) and bottom (−Y) cap fans.
	public static (Vertex[] Vertices, uint[] Indices) BuildCylinder(float height = 1f, float radius = 0.5f, int segments = 16)
	{
		var hy = height * 0.5f;
		var vertices = new List<Vertex>();
		var indices = new List<uint>();
		var sideStart = (uint)vertices.Count;
		for (var j = 0; j <= segments; j++)
		{
			var u = j / (float)segments;
			var theta = u * MathF.Tau;
			var (st, ct) = (MathF.Sin(theta), MathF.Cos(theta));
			var n = new Vector3(ct, 0, st);
			var tan = MakeTangent(n, new Vector3(-st, 0, ct));
			vertices.Add(new Vertex(new Vector3(radius * ct, -hy, radius * st), n, new Vector2(u, 1), tan));
			vertices.Add(new Vertex(new Vector3(radius * ct, hy, radius * st), n, new Vector2(u, 0), tan));
		}
		for (uint j = 0; j < segments; j++)
		{
			var b0 = sideStart + j * 2;
			indices.AddRange([b0, b0 + 1, b0 + 2, b0 + 2, b0 + 1, b0 + 3]);
		}
		AddCap(vertices, indices, hy, radius, segments, top: true);
		AddCap(vertices, indices, -hy, radius, segments, top: false);
		return ([.. vertices], [.. indices]);
	}

	// A cone of `height` along Y (base at −Y, apex at +Y, centred at the origin), base `radius`, `segments` around:
	// smooth slanted side normals (the apex is duplicated per column so each side triangle keeps its own normal) plus
	// a flat −Y base cap fan.
	public static (Vertex[] Vertices, uint[] Indices) BuildCone(float height = 1f, float radius = 0.5f, int segments = 16)
	{
		var hy = height * 0.5f;
		var vertices = new List<Vertex>();
		var indices = new List<uint>();
		var start = (uint)vertices.Count;
		for (var j = 0; j <= segments; j++)
		{
			var u = j / (float)segments;
			var theta = u * MathF.Tau;
			var (st, ct) = (MathF.Sin(theta), MathF.Cos(theta));
			var n = Vector3.Normalize(new Vector3(ct * height, radius, st * height)); // outward + up the slant
			var tan = MakeTangent(n, new Vector3(-st, 0, ct));
			vertices.Add(new Vertex(new Vector3(radius * ct, -hy, radius * st), n, new Vector2(u, 1), tan));
			vertices.Add(new Vertex(new Vector3(0, hy, 0), n, new Vector2(u, 0), tan));
		}
		for (uint j = 0; j < segments; j++)
		{
			var b0 = start + j * 2;
			indices.AddRange([b0, b0 + 1, b0 + 2]); // base[j], apex[j], base[j+1]
		}
		AddCap(vertices, indices, -hy, radius, segments, top: false);
		return ([.. vertices], [.. indices]);
	}

	// A capsule with a cylindrical shaft of `height` along Y (centred at the origin) capped by two `radius`
	// hemispheres, `segments` around and `stacks` per hemisphere. The hemisphere equator rings sit at ±height/2 and
	// the shaft is the band between them; every ring's normal is its hemisphere/radial normal, so they blend smoothly
	// across the joins (no separate shaft geometry needed — the two equator rings span it).
	public static (Vertex[] Vertices, uint[] Indices) BuildCapsule(float height = 1f, float radius = 0.4f, int segments = 16, int stacks = 8)
	{
		var hy = height * 0.5f;
		var rings = new List<(float Y, float RingR, float Cp)>();
		for (var i = 0; i <= stacks; i++) // top hemisphere: phi 0 (pole) → π/2 (equator), centred at +hy
		{
			var phi = i / (float)stacks * (MathF.PI / 2f);
			rings.Add((hy + radius * MathF.Cos(phi), radius * MathF.Sin(phi), MathF.Cos(phi)));
		}
		for (var i = 0; i <= stacks; i++) // bottom hemisphere: phi π/2 (equator) → π (pole), centred at −hy
		{
			var phi = MathF.PI / 2f + i / (float)stacks * (MathF.PI / 2f);
			rings.Add((-hy + radius * MathF.Cos(phi), radius * MathF.Sin(phi), MathF.Cos(phi)));
		}
		var vertices = new List<Vertex>();
		for (var i = 0; i < rings.Count; i++)
		{
			var (y, ringR, cp) = rings[i];
			var sp = MathF.Sqrt(MathF.Max(0f, 1f - cp * cp)); // sin(phi) ≥ 0
			var vv = i / (float)(rings.Count - 1);
			for (var j = 0; j <= segments; j++)
			{
				var u = j / (float)segments;
				var theta = u * MathF.Tau;
				var (st, ct) = (MathF.Sin(theta), MathF.Cos(theta));
				var n = new Vector3(sp * ct, cp, sp * st);
				vertices.Add(new Vertex(new Vector3(ringR * ct, y, ringR * st), n, new Vector2(u, vv), MakeTangent(n, new Vector3(-st, 0, ct))));
			}
		}
		var stride = (uint)(segments + 1);
		var indices = new List<uint>();
		for (uint i = 0; i < rings.Count - 1; i++)
			for (uint j = 0; j < segments; j++)
			{
				var a = i * stride + j;
				var b = a + stride;
				indices.AddRange([a, b, a + 1, a + 1, b, b + 1]);
			}
		return ([.. vertices], [.. indices]);
	}

	// A torus in the XZ plane (the tube swept around +Y): major radius `majorR`, tube radius `minorR`, `majorSeg`
	// segments around the ring and `minorSeg` around the tube. Normals point outward from the tube centre; UVs tile
	// once around each axis.
	public static (Vertex[] Vertices, uint[] Indices) BuildTorus(float majorR = 0.5f, float minorR = 0.2f, int majorSeg = 32, int minorSeg = 16)
	{
		var vertices = new List<Vertex>((majorSeg + 1) * (minorSeg + 1));
		var indices = new List<uint>(majorSeg * minorSeg * 6);
		for (var i = 0; i <= majorSeg; i++)
		{
			var uu = i / (float)majorSeg;
			var u = uu * MathF.Tau;
			var (su, cu) = (MathF.Sin(u), MathF.Cos(u));
			for (var j = 0; j <= minorSeg; j++)
			{
				var vv = j / (float)minorSeg;
				var vAng = vv * MathF.Tau;
				var (sv, cv) = (MathF.Sin(vAng), MathF.Cos(vAng));
				var n = new Vector3(cv * cu, sv, cv * su);
				var pos = new Vector3((majorR + minorR * cv) * cu, minorR * sv, (majorR + minorR * cv) * su);
				vertices.Add(new Vertex(pos, n, new Vector2(uu, vv), MakeTangent(n, new Vector3(-su, 0, cu))));
			}
		}
		var stride = (uint)(minorSeg + 1);
		for (uint i = 0; i < majorSeg; i++)
			for (uint j = 0; j < minorSeg; j++)
			{
				var a = i * stride + j;
				var b = a + stride;
				indices.AddRange([a, b, a + 1, a + 1, b, b + 1]);
			}
		return ([.. vertices], [.. indices]);
	}

	// Get-or-create the unit-sphere midpoint of edge (a,b), caching by the unordered edge key so the two triangles
	// sharing an edge share its midpoint vertex (keeps the icosphere welded).
	private static uint Midpoint(uint a, uint b, List<Vector3> positions, Dictionary<long, uint> cache)
	{
		var key = a < b ? ((long)a << 32) | b : ((long)b << 32) | a;
		if (cache.TryGetValue(key, out var idx))
			return idx;
		idx = (uint)positions.Count;
		positions.Add(Vector3.Normalize((positions[(int)a] + positions[(int)b]) * 0.5f));
		cache[key] = idx;
		return idx;
	}

	// A flat disc cap fan at height `y` facing ±Y — shared by the cylinder (both caps) and the cone (base).
	private static void AddCap(List<Vertex> vertices, List<uint> indices, float y, float radius, int segments, bool top)
	{
		var n = new Vector3(0, top ? 1 : -1, 0);
		var tan = MakeTangent(n, Vector3.UnitX);
		var center = (uint)vertices.Count;
		vertices.Add(new Vertex(new Vector3(0, y, 0), n, new Vector2(0.5f, 0.5f), tan));
		for (var j = 0; j <= segments; j++)
		{
			var theta = j / (float)segments * MathF.Tau;
			var (st, ct) = (MathF.Sin(theta), MathF.Cos(theta));
			vertices.Add(new Vertex(new Vector3(radius * ct, y, radius * st), n, new Vector2(ct * 0.5f + 0.5f, st * 0.5f + 0.5f), tan));
		}
		for (uint j = 0; j < segments; j++)
		{
			var r0 = center + 1 + j;
			if (top)
				indices.AddRange([center, r0, r0 + 1]);
			else
				indices.AddRange([center, r0 + 1, r0]); // reversed so the −Y face winds outward
		}
	}

	// A glTF-convention tangent for a smooth-surface vertex: the surface's eastward ∂pos/∂u direction
	// Gram-Schmidt-orthonormalised against the normal, w = +1. These staple shapes shade from the geometric normal
	// (not a tangent-space normal map), so the bitangent handedness is unused; +X is the degenerate fallback.
	private static Vector4 MakeTangent(Vector3 n, Vector3 du)
	{
		var t = du - n * Vector3.Dot(n, du);
		t = t.LengthSquared() > 1e-12f ? Vector3.Normalize(t) : new Vector3(1, 0, 0);
		return new Vector4(t, 1f);
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
