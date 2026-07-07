// Deferred lighting + shadow mapping — the Shadows demo's terminal lighting pass (Planning.md §7.2). It is
// lighting.slang without the SSAO input: a fullscreen pass that samples the g-buffer (albedo 0, encoded normal
// 1, world position 2) and the sun's shadow map (3), and writes the lit, shadowed result. The shadow test
// reuses the sun's view-projection (a fragment push — the SAME matrix the depth-only shadow pass drew the map
// with, so the projection is consistent): project the world position into the sun's clip space and compare its
// depth against the nearest caster the shadow map recorded. The deferred split is what makes this work within
// the engine's single-push model — the camera transform lives in the g-buffer pass (vertex MVP push) and the
// sun transform lives here (fragment push), one matrix each.

#include "loom_platform.module.slang"

LOOM_BINDING(0) Sampler2D albedoTex;
LOOM_BINDING(1) Sampler2D normalTex;
LOOM_BINDING(2) Sampler2D positionTex;
LOOM_BINDING(3) Sampler2D shadowMap;

struct PushConstants
{
	float4x4 sunViewProj; // world space → the shadow map's clip space
};

LOOM_PUSH_CONSTANT PushConstants pc;

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

// 1.0 = fully lit, lower = in shadow. Projects the world position into light space and compares its depth
// against the nearest caster the shadow map stored (with a small bias to fight shadow acne).
float sunVisibility(float3 worldPos)
{
	float4 lightClip = mul(pc.sunViewProj, float4(worldPos, 1.0));
	// Screen uv of the reprojected point (Y-flipped under WGSL — the shadow map was drawn with the same uncorrected
	// view-projection); depth stays lightClip.z/.w, since the clip-space correction only flips Y, not Z.
	float2 uv = LOOM_CLIP_TO_UV(lightClip);
	float ndcZ = lightClip.z / lightClip.w;

	if (any(uv < 0.0) || any(uv > 1.0) || ndcZ > 1.0)
		return 1.0; // outside the sun's frustum — treat as lit

	// SampleLevel (explicit LOD 0), not Sample: this runs in non-uniform control flow (past the frustum test), which
	// WGSL forbids for the derivative-taking Sample. The shadow map is single-mip, so it is identical on desktop.
	float nearest = shadowMap.SampleLevel(uv, 0.0).r;
	return ndcZ - 0.0025 > nearest ? 0.25 : 1.0; // occluded → keep only ambient
}

[shader("fragment")]
float4 fragmentMain(VsOut input) : SV_Target
{
	float3 albedo = albedoTex.Sample(input.uv).rgb;
	float3 normal = normalize(normalTex.Sample(input.uv).rgb * 2.0 - 1.0);
	float3 worldPos = positionTex.Sample(input.uv).rgb;

	float3 lightDir = normalize(float3(0.45, 0.55, -0.6));
	float ndotl = max(dot(normal, lightDir), 0.0);
	const float ambient = 0.15;
	const float key = 1.8;

	float shadow = sunVisibility(worldPos);
	return float4(albedo * (ambient + ndotl * key * shadow), 1.0);
}
