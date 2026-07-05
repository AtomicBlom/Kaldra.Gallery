// G-buffer technique: the geometry pass writes several colour targets at once (multiple render targets) plus
// depth. Vertex transforms by the app's view-projection (a .Reads constants buffer, clip-space-corrected) and
// the per-object model (the per-draw Uniforms block); the fragment outputs albedo to SV_Target0, the encoded
// world normal to SV_Target1, and world position to SV_Target2, in the same order the pass declares
// .Writes(albedo, normal, position). Deferred lighting samples these — position lets it project into the sun's
// light space for the shadow test without reconstructing from depth.

#include "loom_platform.module.slang"

#include "transform.module.slang"

struct ViewProj { float4x4 viewProj; };           // the app's view-projection, clip-space-corrected
struct PerDraw  { float4x4 model; uint objectId; };  // per-object model + picking id (id unused here)

// No samplers precede them, so the UBOs own Vulkan bindings 0,1; register(b1)/(b2) on DX12 (b0 is the unused
// root-constant slot). The cbuffer packs the model's transpose, so it applies column-form and the normal by the
// inverse of that 3x3 (correct under any scale).
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
	output.albedo = float4(0.82, 0.76, 0.70, 1.0);                      // a warm base colour
	output.normal = float4(normalize(input.normal) * 0.5 + 0.5, 1.0);  // [-1,1] -> [0,1] for an unorm/float target
	output.position = float4(input.worldPos, 1.0);                     // world position for the shadow test
	return output;
}
