// A cut-out drawn by alpha-to-coverage: a grille of thin bars on a card, its alpha 1 on a bar and 0 between them,
// softened over a pixel at each bar's edge. In a multisampled pass with TechniqueDescriptor.AlphaToCoverage that alpha
// becomes the share of the pixel's samples the fragment covers, so the bars' edges resolve as smoothly as geometry's
// rather than stepping as a discard would (the MSAA demo). Lit as lit.slang lights, which it shares its inputs with.

#include "loom_platform.module.slang"

#include "transform.module.slang"

struct ViewProj { float4x4 viewProj; };
struct PerDraw  { float4x4 model; uint objectId; };

LOOM_BINDING(0) ConstantBuffer<ViewProj> view : register(b1);
LOOM_BINDING(1) ConstantBuffer<PerDraw>  draw : register(b2);

static const float Bars = 24.0;    // across the card
static const float Width = 0.18;   // of each bar's repeat

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
	float4 tangent : TANGENT;
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
	// How far into its bar the pixel is, in pixels, from the bar's middle: 1 inside, falling to 0 over a pixel
	float t = input.uv.x * Bars;
	float d = abs(frac(t) - 0.5);
	float pixels = max(fwidth(t), 1e-4);
	float alpha = saturate((Width * 0.5 - d) / pixels + 0.5);

	float3 n = normalize(input.normal);
	float3 lightDir = normalize(float3(0.45, 0.55, -0.6));
	float lit = 0.25 + 1.2 * abs(dot(n, lightDir));
	return float4(float3(0.85, 0.78, 0.62) * lit, alpha);
}
