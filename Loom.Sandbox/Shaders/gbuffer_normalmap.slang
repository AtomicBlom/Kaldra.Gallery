// Normal-mapped G-buffer technique. Like gbuffer.slang it writes the deferred targets — albedo (SV_Target0),
// encoded world normal (SV_Target1), world position (SV_Target2) — so the existing deferred lighting
// (lighting.slang / light_shadow.slang) consumes it unchanged. The difference is the surface: albedo comes from a
// material texture (slot 0) and the per-pixel normal from a tangent-space normal map (slot 1), rotated into world
// space by the TBN frame built from the interpolated normal + vertex tangent. UVs are scaled by TILES so the
// demo's [0,1]-mapped geometry tiles the texture — which exercises the Repeat sampler address mode and the mip chain.

#include "transform.module.slang"

[[vk::binding(0, 0)]] Sampler2D albedoTex; // material slot 0 — base colour (sRGB)
[[vk::binding(1, 0)]] Sampler2D normalTex; // material slot 1 — tangent-space normal map (linear)

struct ViewProj { float4x4 viewProj; };              // the app's view-projection, clip-space-corrected
struct PerDraw  { float4x4 model; uint objectId; };  // per-object model + picking id (id unused here)

// The two samplers own bindings 0,1, so the UBOs follow at 2,3 (register b1/b2; b0 is the unused root-constant slot).
[[vk::binding(2, 0)]] ConstantBuffer<ViewProj> view : register(b1);
[[vk::binding(3, 0)]] ConstantBuffer<PerDraw>  draw : register(b2);

static const float TILES = 3.0; // > 1 so [0,1] UVs tile — exercises Repeat addressing + mip sampling

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
	float3 normal : NORMAL;
	float4 tangent : TANGENT;
	float3 worldPos : POSITION0;
	float2 uv : TEXCOORD0;
};

struct GBuffer
{
	float4 albedo : SV_Target0;
	float4 normal : SV_Target1;
	float4 position : SV_Target2;
};

[shader("vertex")]
VsOut vertexMain(VsIn input)
{
	VsOut output;
	float4 world = mul(draw.model, float4(input.position, 1.0));
	output.position = mul(view.viewProj, world);
	// Normal by the inverse of the model's 3x3 (column-form, correct under any scale); tangent is a direction, so
	// it rides the model's upper-3x3 directly. The handedness (w) passes through for the bitangent reconstruction.
	output.normal = mul(input.normal, inverse3x3((float3x3)draw.model));
	output.tangent = float4(mul((float3x3)draw.model, input.tangent.xyz), input.tangent.w);
	output.worldPos = world.xyz;
	output.uv = input.uv;
	return output;
}

[shader("fragment")]
GBuffer fragmentMain(VsOut input)
{
	float2 uv = input.uv * TILES;
	float3 albedo = albedoTex.Sample(uv).rgb;
	float3 ts = normalTex.Sample(uv).rgb * 2.0 - 1.0; // decode tangent-space normal [0,1] -> [-1,1]

	// TBN: rebuild the bitangent from the glTF-convention handedness, rotate the tangent-space normal to world.
	float3 N = normalize(input.normal);
	float3 T = normalize(input.tangent.xyz);
	float3 B = cross(N, T) * input.tangent.w;
	float3 worldN = normalize(T * ts.x + B * ts.y + N * ts.z);

	GBuffer output;
	output.albedo = float4(albedo, 1.0);
	output.normal = float4(worldN * 0.5 + 0.5, 1.0); // encode [-1,1] -> [0,1] for the lighting pass to decode
	output.position = float4(input.worldPos, 1.0);
	return output;
}
