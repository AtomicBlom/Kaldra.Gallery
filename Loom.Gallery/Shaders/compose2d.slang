// 2D deferred compose — preview (PlanningPhase2.md M14-1). A fullscreen pass that reads the 2D G-buffer
// (albedo 0, encoded normal 1, emissive 2) and lights it with a single fixed directional key light + ambient,
// plus the emissive term. Just enough to see the sprites with normal-mapped relief before the real, data-driven
// 2D light volumes (light2d.slang) replace this compose in M14-3. Output is HDR (the lit target is Rgba16F), so
// bloom/tonemap can follow. Background pixels have albedo 0 (the sprite pass cleared the G-buffer), so they stay
// black regardless of the (undefined) normal there.

#include "loom_platform.module.slang"

Sampler2D albedoTex;
Sampler2D normalTex;
Sampler2D emissiveTex;

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
	float4 albedo = albedoTex.Sample(input.uv);
	float3 normal = normalize(normalTex.Sample(input.uv).rgb * 2.0 - 1.0);
	float3 emissive = emissiveTex.Sample(input.uv).rgb;

	float3 lightDir = normalize(float3(0.3, 0.4, 0.85)); // a fixed key light, front-upper-right
	float ndotl = max(dot(normal, lightDir), 0.0);
	const float ambient = 0.30;

	float3 lit = albedo.rgb * (ambient + ndotl) + emissive;
	return float4(lit, 1.0);
}
