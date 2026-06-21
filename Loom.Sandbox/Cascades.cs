using System.Numerics;

using Loom.Core;
using Loom.Core.Geometry;

namespace Loom.Sandbox;

/// <summary>
/// Host-side cascaded-shadow-map fitting (M15-3). The engine offers only the frustum primitive
/// (<see cref="Matrix4x4FrustumExtensions.GetFrustumCorners"/>); the policy of how many cascades and
/// where to split lives here, in the app — matching the "you hand me the views" invariant (the engine never
/// decides the light's framing). Produces, per cascade, a neutral ortho <see cref="Camera"/> fitted to a slice
/// of the camera frustum (bounding-sphere fit → rotation-stable, texel-snapped → shimmer-free), plus the far
/// view-depth each cascade covers (for the lighting pass's per-pixel cascade selection).
/// </summary>
internal static class Cascades
{
	/// <summary>
	/// Fit <paramref name="cascades"/>.Length cascades to the camera frustum for sun direction
	/// <paramref name="lightDir"/> (pointing toward the sun). <paramref name="near"/>/<paramref name="far"/> are
	/// the camera's near/far planes (the shadowed depth range); <paramref name="resolution"/> is each cascade
	/// map's pixel size (for texel snapping). Splits use a practical (log↔uniform) blend. Writes the fitted ortho
	/// cameras into <paramref name="cascades"/> and each cascade's far view-depth into <paramref name="splits"/>.
	/// </summary>
	public static void Compute(in Camera camera, Vector3 lightDir, float near, float far, float resolution, Span<Camera> cascades, Span<float> splits, float lambda = 0.6f)
	{
		var count = cascades.Length;
		lightDir = Vector3.Normalize(lightDir);

		// Practical split scheme: blend a uniform and a logarithmic distribution of the view-depth range.
		Span<float> splitDepths = stackalloc float[count + 1];
		splitDepths[0] = near;
		for (var i = 1; i <= count; i++)
		{
			var p = (float)i / count;
			var uniform = near + (far - near) * p;
			var logarithmic = near * MathF.Pow(far / near, p);
			splitDepths[i] = float.Lerp(uniform, logarithmic, lambda);
		}

		// The full camera frustum's 8 world-space corners (near quad 0..3 at z=0, far quad 4..7 at z=1). A point
		// at view-depth d on edge k is lerp(near[k], far[k], (d-near)/(far-near)) — both planes at fixed depths.
		var corners = camera.ViewProjection.GetFrustumCorners();
		var up = MathF.Abs(lightDir.Y) > 0.99f ? Vector3.UnitZ : Vector3.UnitY;
		var range = far - near;

		for (var c = 0; c < count; c++)
		{
			var t0 = (splitDepths[c] - near) / range;
			var t1 = (splitDepths[c + 1] - near) / range;

			// The slice's 8 corners, and their bounding sphere (center = mean, radius = farthest corner).
			Span<Vector3> slice = stackalloc Vector3[8];
			var center = Vector3.Zero;
			for (var k = 0; k < 4; k++)
			{
				slice[k] = Vector3.Lerp(corners[k], corners[k + 4], t0);
				slice[k + 4] = Vector3.Lerp(corners[k], corners[k + 4], t1);
				center += slice[k] + slice[k + 4];
			}
			center /= 8f;
			var radius = 0f;
			for (var k = 0; k < 8; k++)
				radius = MathF.Max(radius, Vector3.Distance(center, slice[k]));
			radius = MathF.Ceiling(radius * 16f) / 16f; // quantize the radius so it doesn't wobble frame to frame

			// Texel-snap the sphere center to the shadow-map grid in light space (kills crawling under motion).
			var texelsPerUnit = resolution / (radius * 2f);
			var lightView = Matrix4x4.CreateLookAt(center, center - lightDir, up);
			var snap = Vector3.Transform(center, lightView);
			snap.X = MathF.Floor(snap.X * texelsPerUnit) / texelsPerUnit;
			snap.Y = MathF.Floor(snap.Y * texelsPerUnit) / texelsPerUnit;
			Matrix4x4.Invert(lightView, out var invLightView);
			center = Vector3.Transform(snap, invLightView);

			// Sun eye on the sunward side, looking back at the slice; ortho sized to the sphere, with depth margin
			// in front (toward the sun) to catch casters above the slice.
			var eye = center + lightDir * (radius * 2f);
			var sunView = Matrix4x4.CreateLookAt(eye, center, up);
			var sunProj = Matrix4x4.CreateOrthographic(radius * 2f, radius * 2f, 0f, radius * 4f);
			cascades[c] = new Camera(sunView, sunProj);
			splits[c] = splitDepths[c + 1];
		}
	}
}
