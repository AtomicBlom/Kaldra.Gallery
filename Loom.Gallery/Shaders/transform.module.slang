// Shared transform helpers for the mesh techniques. Textually included via `#include "transform.module.slang"`
// (the *.module.slang naming keeps it out of the standalone shader-compile glob).

// Inverse of a 3x3 matrix via the adjugate / determinant. Hand-written rather than the Slang `inverse()`
// builtin, whose lowering DXC has rejected before — keeps SPIR-V and DXIL identical. `inv` is filled directly
// as the adjugate (transpose of the cofactor matrix), so `inv / det` is the true inverse.
float3x3 inverse3x3(float3x3 m)
{
	float3x3 inv;
	inv[0][0] = m[1][1] * m[2][2] - m[1][2] * m[2][1];
	inv[0][1] = m[0][2] * m[2][1] - m[0][1] * m[2][2];
	inv[0][2] = m[0][1] * m[1][2] - m[0][2] * m[1][1];
	inv[1][0] = m[1][2] * m[2][0] - m[1][0] * m[2][2];
	inv[1][1] = m[0][0] * m[2][2] - m[0][2] * m[2][0];
	inv[1][2] = m[0][2] * m[1][0] - m[0][0] * m[1][2];
	inv[2][0] = m[1][0] * m[2][1] - m[1][1] * m[2][0];
	inv[2][1] = m[0][1] * m[2][0] - m[0][0] * m[2][1];
	inv[2][2] = m[0][0] * m[1][1] - m[0][1] * m[1][0];
	float det = m[0][0] * inv[0][0] + m[0][1] * inv[1][0] + m[0][2] * inv[2][0];
	return inv * (1.0 / det);
}

// The normal matrix for a model whose 3x3 linear part is `m`, in the row-vector convention positions use as
// `worldNormal = mul(localNormal, normalMatrixRow(m))`. Equal to transpose(inverse(m)); reduces to identity for
// an identity model and to a pure rotation for rotation + uniform scale, so it stays correct under non-uniform scale.
float3x3 normalMatrixRow(float3x3 m)
{
	return transpose(inverse3x3(m));
}
