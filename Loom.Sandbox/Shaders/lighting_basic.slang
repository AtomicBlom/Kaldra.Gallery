// Minimal deferred lighting (M15-5): albedo/normal/position g-buffer → a single directional key + ambient, no
// shadows. Used by the foliage demo to shade the instanced field cheaply. Background pixels (albedo 0) stay dark.

#include "loom_platform.module.slang"

LOOM_BINDING(0) Sampler2D albedoTex;
LOOM_BINDING(1) Sampler2D normalTex;
LOOM_BINDING(2) Sampler2D positionTex;

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
	output.position = float4(uv * 2.0 - 1.0, 0.0, 1.0);
	return output;
}

[shader("fragment")]
float4 fragmentMain(VsOut input) : SV_Target
{
	float3 albedo = albedoTex.Sample(input.uv).rgb;
	float3 normal = normalize(normalTex.Sample(input.uv).rgb * 2.0 - 1.0);
	float3 lightDir = normalize(float3(0.5, 0.7, -0.45));
	float ndotl = max(dot(normal, lightDir), 0.0);
	return float4(albedo * (0.25 + ndotl * 1.1), 1.0);
}
