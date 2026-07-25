// Object-id / picking pass. Draws opaque geometry writing each object's id into a single-channel target — the
// seed source the JFA outline grows a border from ("same buffer, new consumer"). The id is per-object data: it
// rides the per-draw block, so each object writes its own id and the readback at the cursor decodes which object
// is hovered. Occlusion would normally reuse the opaque depth, but a single convex object is its own silhouette,
// so no depth test is needed here.

#include "loom_platform.module.slang"

#include "transform.module.slang"

struct ViewProj { float4x4 viewProj; };              // the app's view-projection, clip-space-corrected
struct PerDraw  { float4x4 model; uint objectId; };  // per-object model + the picking id this pass writes

// No samplers precede them, so the UBOs own bindings 0,1 (register b1/b2; b0 is the unused root-constant slot).
LOOM_BINDING(0) ConstantBuffer<ViewProj> view : register(b1);
LOOM_BINDING(1) ConstantBuffer<PerDraw>  draw : register(b2);

struct VsIn
{
	// HLSL semantics alongside the vk::location qualifiers (see lit.slang): Slang pins the SPIR-V input
	// locations from vk::location (Vulkan unchanged) AND emits the semantics for the HLSL/DXIL target (DX12).
	[[vk::location(0)]] float3 position : POSITION;
	[[vk::location(1)]] float3 normal   : NORMAL;
	[[vk::location(2)]] float2 uv        : TEXCOORD0;
	[[vk::location(3)]] float4 tangent   : TANGENT;
};

struct VsOut
{
	float4 position : SV_Position;
	float4 tangent : TANGENT; // consumed only to satisfy the shared 4-attribute vertex layout
	float3 normal : NORMAL;
	float2 uv : TEXCOORD0;
};

[shader("vertex")]
VsOut vertexMain(VsIn input)
{
	VsOut output;
	float4 world = mul(draw.model, float4(input.position, 1.0));
	output.position = mul(view.viewProj, world);
	output.tangent = input.tangent;
	output.normal = input.normal;
	output.uv = input.uv;
	return output;
}

[shader("fragment")]
float fragmentMain(VsOut input) : SV_Target
{
	return (float)draw.objectId; // this object's id; background stays 0 (the target is cleared)
}
