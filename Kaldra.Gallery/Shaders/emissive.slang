// Emissive geometry — a self-lit, far-above-1.0 HDR surface, used by the bloom demo so there is something that
// genuinely glows. Same vertex path as lit.slang (view-projection constant + per-draw model, per-face normal
// colour), but the fragment ignores lighting and emits its colour scaled well past the display range, so the
// bloom threshold catches it strongly and the mip pyramid spreads a bright halo. (A real engine would drive the
// emission from a material; here it's a constant to keep the demo to one idea: bright pixels bloom.)

#include "kaldra_platform.module.slang"

#include "transform.module.slang"

struct ViewProj { float4x4 viewProj; };           // the app's view-projection, clip-space-corrected
struct PerDraw  { float4x4 model; uint objectId; };  // per-object model + picking id (id unused by emissive)

// Bound as in lit.slang: the view, then the draw.
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
	float2 uv : TEXCOORD0; // passed through (the engine's vertex layout provides it) though shading ignores it
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
	float3 colour = normalize(input.normal) * 0.5 + 0.5; // distinct per-face colour
	const float emission = 5.0;                          // >> 1 so it blooms hard
	return float4(colour * emission, 1.0);
}
