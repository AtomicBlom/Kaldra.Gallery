// Depth-only technique (shadow maps). Drawn from a light's view into the shadow map: it writes only depth — no
// colour target, no fragment output — so each shadow-map texel holds the distance from the light to the nearest
// caster. Deferred lighting then compares against it. The vertex transforms by the view-projection the pass
// .Reads (here the light's, clip-space-corrected) and the per-object model, so casters land in the light's clip
// space; a camera depth pre-pass reuses the same technique with the camera's view-projection instead.

#include "loom_platform.module.slang"

#include "transform.module.slang"

struct ViewProj { float4x4 viewProj; };              // the light's (or camera's) view-projection, clip-space-corrected
struct PerDraw  { float4x4 model; uint objectId; };  // per-object model (id unused here)

// No samplers precede them, so the UBOs own bindings 0,1.
ConstantBuffer<ViewProj> view;
ConstantBuffer<PerDraw>  draw;

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
	// Passed through (unused by the fragment) only so the vertex stage consumes the shared mesh layout's
	// normal/uv attributes — keeps validation quiet without a per-technique vertex-input layout.
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
void fragmentMain(VsOut input)
{
	// No colour output — depth is written by the fixed-function depth test/write alone.
}
