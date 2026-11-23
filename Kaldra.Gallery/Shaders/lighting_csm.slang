// Deferred lighting with cascaded shadow maps (M15-3). Samples the g-buffer (albedo/normal/world-position) +
// three cascade depth maps, and reads the per-frame cascade matrices/splits from a constant buffer (the M15-0
// uniform seam — a cbuffer matrix is laid out consistently across SPIR-V/DXIL, unlike a structured buffer, so
// projecting world positions into a cascade's clip space works on both backends as the single-map shadow does).
// Selects a cascade per pixel by view-space depth, projects, and tests against that cascade's map.

#include "kaldra_platform.module.slang"

static const int CASCADE_COUNT = 3;

Sampler2D albedoTex;
Sampler2D normalTex;
Sampler2D positionTex;
Sampler2D cascade0;
Sampler2D cascade1;
Sampler2D cascade2;

// The cascade split far-depths (.xyz) + cascade-tint flag (.w) — an app-written parameter buffer the host fills
// from its fitted cascades. Bound at uniform slot 0 (host data the pass .Reads), ahead of the in-closure matrix push.
struct CsmSplits
{
	float4 splits;
};
ConstantBuffer<CsmSplits> csmSplits;

struct CsmParams
{
	float4x4 cascadeViewProj[CASCADE_COUNT]; // world → each cascade's light clip space
	float4 eye;                              // camera world position (.xyz)
	float4 forward;                          // camera forward, normalized (.xyz) — for the view-depth metric
	float4 sunDir;                           // direction toward the sun (.xyz), key intensity (.w)
	float4 sunColor;                         // sun light colour (.rgb) — warm at dawn/dusk, white at noon
	float4 ambientColor;                     // sky-tinted ambient colour (.rgb), dynamic by time of day
};
ConstantBuffer<CsmParams> csm;

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
	output.position = KALDRA_FULLSCREEN_POS(uv);
	return output;
}

float sampleCascade(int cascade, float2 uv)
{
	// SampleLevel (explicit LOD 0), not Sample: the cascade selection makes this non-uniform control flow, which WGSL
	// forbids for the derivative-taking Sample. The shadow maps are single-mip, so the result is identical on desktop.
	if (cascade == 0)
		return cascade0.SampleLevel(uv, 0.0).r;
	if (cascade == 1)
		return cascade1.SampleLevel(uv, 0.0).r;
	return cascade2.SampleLevel(uv, 0.0).r;
}

// 1.0 = lit, 0.25 = in shadow (ambient only). Projects the world position into the chosen cascade's clip space
// and compares depth, with a per-cascade bias (farther cascades cover more world per texel → need more bias).
float sunVisibility(float3 worldPos, int cascade)
{
	float4 lightClip = mul(csm.cascadeViewProj[cascade], float4(worldPos, 1.0));
	// Screen uv of the reprojected point (Y-flipped under WGSL, since the shadow map was rendered with the same
	// uncorrected view-projection); depth stays lightClip.z/.w — the clip-space correction only flips Y, not Z.
	float2 uv = KALDRA_CLIP_TO_UV(lightClip);
	float ndcZ = lightClip.z / lightClip.w;
	if (any(uv < 0.0) || any(uv > 1.0) || ndcZ > 1.0)
		return 1.0;
	float bias = 0.0015 * (cascade + 1);
	float nearest = sampleCascade(cascade, uv);
	return ndcZ - bias > nearest ? 0.25 : 1.0;
}

[shader("fragment")]
float4 fragmentMain(VsOut input) : SV_Target
{
	float3 albedo = albedoTex.Sample(input.uv).rgb;
	float3 normal = normalize(normalTex.Sample(input.uv).rgb * 2.0 - 1.0);
	float3 worldPos = positionTex.Sample(input.uv).rgb;

	// Cascade selection by view-space depth (distance along the camera forward axis).
	float viewDepth = dot(worldPos - csm.eye.xyz, csm.forward.xyz);
	int cascade = 0;
	if (viewDepth > csmSplits.splits.x)
		cascade = 1;
	if (viewDepth > csmSplits.splits.y)
		cascade = 2;

	float3 lightDir = normalize(csm.sunDir.xyz);
	float ndotl = max(dot(normal, lightDir), 0.0);
	float keyIntensity = csm.sunDir.w;
	float shadow = sunVisibility(worldPos, cascade);
	// Dynamic ambient (sky-tinted) + the sun's coloured key light; the shadow term removes the key, not ambient.
	float3 lit = albedo * (csm.ambientColor.rgb + csm.sunColor.rgb * (ndotl * keyIntensity * shadow));

	// Debug: tint the lit pixel by which cascade it sampled (red/green/blue) — the M15-7 cascade visualization.
	if (csmSplits.splits.w > 0.5)
	{
		float3 tint = cascade == 0 ? float3(1.0, 0.4, 0.4) : cascade == 1 ? float3(0.4, 1.0, 0.4) : float3(0.4, 0.4, 1.0);
		lit *= tint;
	}
	return float4(lit, 1.0);
}
