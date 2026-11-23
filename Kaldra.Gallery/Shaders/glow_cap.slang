// A glowing mushroom cap — a self-lit red toadstool dome, additively blended so it blooms, but shaded by the
// surface normal so the DOME shape reads (brighter on the crown, dimmer on the underside) rather than a flat
// blob. The distinct red glow separates the ground-anchored mushroom caps from the warm yellow-green airborne
// fireflies. Same vertex path as emissive.slang (view-projection + per-draw model); HDR emission feeds the bloom.

#include "kaldra_platform.module.slang"

#include "transform.module.slang"

struct ViewProj { float4x4 viewProj; };              // the app's view-projection, clip-space-corrected
struct PerDraw  { float4x4 model; uint objectId; };  // per-object model + picking id (id unused)

ConstantBuffer<ViewProj> view;
ConstantBuffer<PerDraw>  draw;

struct VsIn
{
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
	output.normal = mul(input.normal, inverse3x3((float3x3)draw.model));
	output.uv = input.uv;
	return output;
}

[shader("fragment")]
float4 fragmentMain(VsOut input) : SV_Target
{
	float3 N = normalize(input.normal);
	float crown = saturate(N.y * 0.5 + 0.5);           // 1 on the top of the dome, ~0 on the underside
	float3 cap = float3(1.0, 0.20, 0.13);               // red toadstool
	float emission = 1.2 + 1.8 * crown;                 // glows, brightest on the crown (HDR → blooms); moderate so the dome shape reads
	return float4(cap * emission, 1.0);
}
