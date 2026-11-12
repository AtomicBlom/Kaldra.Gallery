// M15-2 compute pre-skin: transform a skinned mesh's bind-pose vertices by the per-frame bone palette ONCE
// into a plain MeshStandard-shaped buffer, which the depth / g-buffer / shadow-cascade passes then draw with
// their existing techniques (the payoff of compute-pre-skin: no skinned variants of those shaders). One thread
// per vertex.
//
// EVERYTHING is a ByteAddressBuffer with EXPLICIT byte offsets — deliberately layout-agnostic. A
// StructuredBuffer<T> (or a matrix in one) is laid out differently by Slang's SPIR-V (std430: float3 padded to
// 16) vs DXIL (tight C packing), which corrupted both the 80-byte input read and the 48-byte output write on
// Vulkan while DX12 was correct. Raw byte Load/Store at fixed offsets reads/writes identically on both backends.
//
// Declared in DispatchSkin's bind order: the bind-pose verts (read), the bone palette (read), the skinned output
// (write). On Vulkan all three are storage buffers; on DX12 they bind as root SRVs + a root UAV by GPU address — so
// the compute entry must be `computeMain` for the build's DXIL emit.

#include "loom_platform.module.slang"

ByteAddressBuffer inVertices;     // SkinnedVertex, 80 bytes: pos@0 nrm@12 uv@24 tan@32 joints@48 weights@64
ByteAddressBuffer bonePalette;    // row-major float4x4 per joint, 64 bytes: row r at joint*64 + r*16
RWByteAddressBuffer outVertices;  // MeshStandard, 48 bytes: pos@0 nrm@12 uv@24 tan@32

struct PushConstants
{
	float vertexCount; // pushed as a float (exact for any realistic count); cast to uint below
};
LOOM_PUSH_CONSTANT PushConstants pc;

// One row of joint j's palette matrix (the CPU uploads System.Numerics row-major matrices, so row r is the
// 16-byte block at joint*64 + r*16). palette[j] = inverseBind[j]·jointWorld[j], so the skinned position is v·palette.
float4 paletteRow(uint joint, uint row)
{
	return asfloat(bonePalette.Load4(joint * 64 + row * 16));
}

[shader("compute")]
[numthreads(64, 1, 1)]
void computeMain(uint3 id : SV_DispatchThreadID)
{
	uint i = id.x;
	if (i >= (uint)pc.vertexCount)
		return;

	uint inBase = i * 80;
	float3 position = asfloat(inVertices.Load3(inBase + 0));
	float3 normal = asfloat(inVertices.Load3(inBase + 12));
	float2 uv = asfloat(inVertices.Load2(inBase + 24));
	float4 tangent = asfloat(inVertices.Load4(inBase + 32));
	uint4 j = inVertices.Load4(inBase + 48);
	float4 w = asfloat(inVertices.Load4(inBase + 64));

	// Blend the four bone matrices' rows by weight, then form the row-vector products explicitly:
	//   (v·M) = v.x·row0 + v.y·row1 + v.z·row2 + v.w·row3   (v a row vector, M's rows row0..row3).
	float4 r0 = w.x * paletteRow(j.x, 0) + w.y * paletteRow(j.y, 0) + w.z * paletteRow(j.z, 0) + w.w * paletteRow(j.w, 0);
	float4 r1 = w.x * paletteRow(j.x, 1) + w.y * paletteRow(j.y, 1) + w.z * paletteRow(j.z, 1) + w.w * paletteRow(j.w, 1);
	float4 r2 = w.x * paletteRow(j.x, 2) + w.y * paletteRow(j.y, 2) + w.z * paletteRow(j.z, 2) + w.w * paletteRow(j.w, 2);
	float4 r3 = w.x * paletteRow(j.x, 3) + w.y * paletteRow(j.y, 3) + w.z * paletteRow(j.z, 3) + w.w * paletteRow(j.w, 3);

	float4 p = float4(position, 1.0);
	float3 skinnedPos = (p.x * r0 + p.y * r1 + p.z * r2 + p.w * r3).xyz;
	// Normal/tangent use the upper-left 3×3 (rotation/scale): n·M3 = n.x·row0.xyz + n.y·row1.xyz + n.z·row2.xyz.
	float3 skinnedNormal = normalize(normal.x * r0.xyz + normal.y * r1.xyz + normal.z * r2.xyz);
	float3 skinnedTangent = normalize(tangent.x * r0.xyz + tangent.y * r1.xyz + tangent.z * r2.xyz);

	uint outBase = i * 48;
	outVertices.Store3(outBase + 0, asuint(skinnedPos));
	outVertices.Store3(outBase + 12, asuint(skinnedNormal));
	outVertices.Store2(outBase + 24, asuint(uv));
	outVertices.Store4(outBase + 32, asuint(float4(skinnedTangent, tangent.w)));
}
