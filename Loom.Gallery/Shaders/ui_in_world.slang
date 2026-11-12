// Forward technique for the in-world UI panel quad — samples the composited UI panel texture (rendered by the
// "ui.panel" pass into an offscreen target) using the mesh UV and outputs with premultiplied alpha blending so
// the panel's anti-aliased rounded corners composite correctly over the background.

#include "loom_platform.module.slang"

#include "transform.module.slang"

Sampler2D panelTex;     // the composited UI panel texture (slot 0, linear filter)

struct ViewProj { float4x4 viewProj; };
struct PerDraw  { float4x4 model; uint objectId; };

ConstantBuffer<ViewProj> view;
ConstantBuffer<PerDraw>  draw;

struct VsIn
{
	[[vk::location(0)]] float3 position : POSITION;
	[[vk::location(1)]] float3 normal   : NORMAL;
	[[vk::location(2)]] float2 uv       : TEXCOORD0;
	[[vk::location(3)]] float4 tangent  : TANGENT;
};

struct VsOut
{
	float4 position : SV_Position;
	float2 uv       : TEXCOORD0;
	float4 tangent  : TANGENT;  // carried to satisfy the shared 4-attribute vertex layout
};

[shader("vertex")]
VsOut vertexMain(VsIn input)
{
	VsOut output;
	float4 world = mul(draw.model, float4(input.position, 1.0));
	output.position = mul(view.viewProj, world);
	output.uv = input.uv;
	output.tangent = input.tangent;
	return output;
}

[shader("fragment")]
float4 fragmentMain(VsOut input) : SV_Target
{
	// Sample the UI panel texture. The panel was composited in premultiplied alpha, so no further
	// premultiplication is needed — the target's blend equation is (src=ONE, dst=ONE_MINUS_SRC_ALPHA).
	return panelTex.Sample(input.uv);
}
