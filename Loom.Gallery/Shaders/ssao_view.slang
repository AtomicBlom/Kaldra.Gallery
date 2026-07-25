// SSAO visualisation — the SSAO demo's terminal pass. It samples the g-buffer albedo (0) and the occlusion
// term the ssao pass wrote (1) and outputs albedo darkened by occlusion, so the contact shadows SSAO produces
// (where the cube meets the floor, in concave corners) are directly visible without a full lighting pass.
// Background albedo is 0, so it stays black. This isolates "what SSAO computes" as its own legible demo.

#include "loom_platform.module.slang"

LOOM_BINDING(0) Sampler2D albedoTex;
LOOM_BINDING(1) Sampler2D aoTex;

struct VsOut
{
	float4 position : SV_Position;
	float2 uv : TEXCOORD0;
};

[shader("vertex")]
VsOut vertexMain(uint vertexId : SV_VertexID)
{
	float2 uv = float2((vertexId << 1) & 2, vertexId & 2);

	VsOut output;
	output.uv = uv;
	output.position = LOOM_FULLSCREEN_POS(uv);
	return output;
}

[shader("fragment")]
float4 fragmentMain(VsOut input) : SV_Target
{
	float3 albedo = albedoTex.Sample(input.uv).rgb;
	float ao = aoTex.Sample(input.uv).r;
	return float4(albedo * ao, 1.0); // occlusion darkens the surface; crevices read as contact shadow
}
