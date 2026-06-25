// Textured G-buffer: like gbuffer.slang, but albedo comes from a material's base-colour texture sampled at the
// mesh's NATIVE uvs (no tiling) — the right fit for an atlas/colormap where each face indexes a flat palette
// region (the Kenney castle kit's shared colormap). The world normal is the interpolated vertex normal (these
// kits carry no normal map). Writes the same deferred targets — albedo (SV_Target0), encoded world normal
// (SV_Target1), world position (SV_Target2) — so the existing deferred lighting consumes it unchanged.

#include "transform.module.slang"

[[vk::binding(0, 0)]] Sampler2D albedoTex; // material slot 0 — base colour (sRGB)

struct ViewProj { float4x4 viewProj; };              // the app's view-projection, clip-space-corrected
struct PerDraw  { float4x4 model; uint objectId; };  // per-object model + picking id (id unused here)

// The sampler owns binding 0, so the UBOs follow at 1,2 (register b1/b2; b0 is the unused root-constant slot).
[[vk::binding(1, 0)]] ConstantBuffer<ViewProj> view : register(b1);
[[vk::binding(2, 0)]] ConstantBuffer<PerDraw>  draw : register(b2);

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
	output.tangent = input.tangent;
	output.normal = mul(input.normal, inverse3x3((float3x3)draw.model));
	output.worldPos = world.xyz;
	output.uv = input.uv;
	return output;
}

[shader("fragment")]
GBuffer fragmentMain(VsOut input)
{
	GBuffer output;
	output.albedo = float4(albedoTex.Sample(input.uv).rgb, 1.0);          // colormap texel at the native uv
	output.normal = float4(normalize(input.normal) * 0.5 + 0.5, 1.0);    // [-1,1] -> [0,1] for the lighting pass
	output.position = float4(input.worldPos, 1.0);                       // world position for the shadow test
	return output;
}
