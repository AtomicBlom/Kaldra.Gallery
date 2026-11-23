// Transparent forward technique — the .Blends counterpart to the opaque g-buffer pass. It draws the same cube
// geometry again, but instead of producing colour it *accumulates* a translucent tint onto the already-lit target
// (the pass .Blends(lit)). It .ReadsDepth(depth): the depth buffer the opaque pass wrote is bound read-only, so
// this draw is depth-tested against the solid cube (no z-fighting, no writes), and the alpha blend (src-alpha over)
// the backend enables for a Blend technique lays the tint over the lit surface.

#include "kaldra_platform.module.slang"

#include "transform.module.slang"

struct ViewProj { float4x4 viewProj; };              // the app's view-projection, clip-space-corrected
struct PerDraw  { float4x4 model; uint objectId; };  // per-object model + picking id (id unused here)

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
	output.uv = input.uv; // passed through so the vertex stage consumes the uv attribute (location 2)
	return output;
}

[shader("fragment")]
float4 fragmentMain(VsOut input) : SV_Target
{
	// A glassy blue tint; the alpha drives the over-blend (0.5 → half tint, half lit). A faint
	// normal-based facet keeps the translucency reading as a surface rather than a flat wash.
	float facet = normalize(input.normal).y * 0.15 + 0.85;
	return float4(float3(0.2, 0.45, 0.95) * facet, 0.5);
}
