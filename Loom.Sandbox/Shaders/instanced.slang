// Instanced forward lighting — the same single directional key + ambient shading as lit.slang, but the
// per-object world matrix arrives as a PER-INSTANCE vertex stream (VertexLayout.Transform: four ITRANSFORM
// rows at binding 1) instead of being baked into the geometry. One mesh, thousands of instances, one draw.
// The camera's view-projection arrives as a constants buffer the pass .Reads (the app composed the engine's
// ClipSpaceCorrection into it); the instance matrix multiplies the local vertex into world space first.

#include "loom_platform.module.slang"

#include "transform.module.slang"

struct ViewProj
{
	float4x4 viewProj; // the app's view-projection, clip-space-corrected
};

LOOM_BINDING(0) ConstantBuffer<ViewProj> view : register(b1);

struct VsIn
{
	// Per-vertex (binding 0 — VertexLayout.MeshStandard), locations 0..3 exactly as lit.slang. The tangent
	// (location 3) is unused by this shader but declared so the explicit locations line up with the layout.
	[[vk::location(0)]] float3 position : POSITION;
	[[vk::location(1)]] float3 normal   : NORMAL;
	[[vk::location(2)]] float2 uv        : TEXCOORD0;
	[[vk::location(3)]] float4 tangent   : TANGENT;

	// Per-instance world matrix rows (binding 1 — VertexLayout.Transform). Vulkan locations continue after the
	// per-vertex attributes (4..7); the DX12 backend binds these as ITRANSFORM0..3 on input slot 1. Uploaded
	// as a row-major System.Numerics.Matrix4x4 (each row a float4), so the four rows reconstruct the matrix.
	[[vk::location(4)]] float4 itransform0 : ITRANSFORM0;
	[[vk::location(5)]] float4 itransform1 : ITRANSFORM1;
	[[vk::location(6)]] float4 itransform2 : ITRANSFORM2;
	[[vk::location(7)]] float4 itransform3 : ITRANSFORM3;
};

struct VsOut
{
	float4 position : SV_Position;
	float3 normal : NORMAL;
	float2 uv : TEXCOORD0; // passed through (so the per-vertex UV attribute is consumed) though shading ignores it
};

[shader("vertex")]
VsOut vertexMain(VsIn input)
{
	VsOut output;
	// Rebuild the instance's world matrix from its four rows (HLSL float4x4(r0..r3) sets them as rows, matching
	// the uploaded row-major Matrix4x4). Apply it row-vector style (world = local · model), then the view-proj constant
	// the same column-form way lit.slang does (the uniform's column-major packing transposes it back to
	// clip = world · VP) — so the same source renders identically on Vulkan and DX12.
	float4x4 model = float4x4(input.itransform0, input.itransform1, input.itransform2, input.itransform3);
	float4 world = mul(float4(input.position, 1.0), model);
	output.position = mul(view.viewProj, world);
	output.normal = mul(input.normal, normalMatrixRow((float3x3)model));
	output.uv = input.uv;
	return output;
}

[shader("fragment")]
float4 fragmentMain(VsOut input) : SV_Target
{
	float3 n = normalize(input.normal);
	float3 albedo = n * 0.5 + 0.5;                          // distinct per-face colour, like lit.slang
	float3 lightDir = normalize(float3(0.45, 0.55, -0.6));   // matches the sandbox sun
	float ndotl = max(dot(n, lightDir), 0.0);

	const float ambient = 0.15;
	const float key = 1.5;
	return float4(albedo * (ambient + ndotl * key), 1.0);
}
