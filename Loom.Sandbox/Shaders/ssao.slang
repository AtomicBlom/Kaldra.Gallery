// Screen-space ambient occlusion (Planning.md §7.6). A pass that samples the G-buffer's world position (0)
// and normal (1) and writes a per-pixel occlusion term; deferred lighting then samples it and darkens the
// ambient term, giving contact shadows (e.g. where the cube meets the floor). Inserting this effect is just
// adding edges — gbuffer → ssao → lighting — and the engine re-derives ordering and barriers (§7.6).
//
// Hemisphere SSAO entirely in world space: for each pixel, jitter a kernel of directions into the normal-
// oriented hemisphere, reproject each sample with the camera's view-projection (the fragment push), and read
// the surface position there; if that surface sits in front of the sample point (and within range), the
// sample is occluded. A per-pixel rotation trades banding for noise, which the following ssao.blur pass then
// smooths into clean contact shadows (§7.6). Position's .a flags geometry vs background.

#include "loom_platform.module.slang"

LOOM_BINDING(0) Sampler2D positionTex;
LOOM_BINDING(1) Sampler2D normalTex;

struct PushConstants
{
	float4x4 viewProj; // world → clip, to reproject sample points to screen
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

static const int kKernel = 16;
// Tangent-space hemisphere directions (z = along the normal), weighted toward the centre.
static const float3 kSamples[16] = {
	float3(0.04, 0.02, 0.05), float3(-0.06, 0.05, 0.08), float3(0.09, -0.07, 0.06), float3(-0.03, -0.09, 0.10),
	float3(0.12, 0.10, 0.14), float3(-0.14, 0.08, 0.12), float3(0.10, -0.15, 0.16), float3(-0.11, -0.12, 0.20),
	float3(0.22, 0.05, 0.18), float3(-0.05, 0.24, 0.22), float3(0.18, -0.20, 0.26), float3(-0.24, -0.10, 0.30),
	float3(0.30, 0.22, 0.34), float3(-0.28, 0.26, 0.40), float3(0.20, -0.34, 0.44), float3(-0.36, -0.30, 0.50),
};

float hash(float2 p)
{
	return frac(sin(dot(p, float2(12.9898, 78.233))) * 43758.5453);
}

[shader("fragment")]
float fragmentMain(VsOut input) : SV_Target
{
	static const float radius = 0.3;    // world units; a small fraction of a feature (the cube is 1 unit) so AO stays in the crease
	static const float intensity = 1.4;
	static const float bias = 0.025;    // world units of view-space depth (see the in-front test below)

	float4 origin = positionTex.Sample(input.uv);
	if (origin.a < 0.5)
		return 1.0; // background — fully unoccluded

	float3 p = origin.xyz;
	float3 n = normalize(normalTex.Sample(input.uv).rgb * 2.0 - 1.0);

	// Per-pixel rotated tangent basis (Gram-Schmidt) so the fixed kernel varies across the image.
	float angle = hash(input.uv) * 6.2831853;
	float3 randVec = float3(cos(angle), sin(angle), 0.0);
	float3 t = normalize(randVec - n * dot(randVec, n));
	float3 b = cross(n, t);
	float3x3 tbn = float3x3(t, b, n);

	float occlusion = 0.0;
	for (int i = 0; i < kKernel; i++)
	{
		float3 dir = mul(kSamples[i], tbn);           // tangent → world
		float3 q = p + dir * radius;                  // a point in the hemisphere

		float4 qClip = mul(pc.viewProj, float4(q, 1.0));
		float2 uv = qClip.xy / qClip.w * 0.5 + 0.5;
		if (any(uv < 0.0) || any(uv > 1.0))
			continue;

		float4 surf = positionTex.Sample(uv);
		if (surf.a < 0.5)
			continue;                                 // sample landed on background

		// Falloff by world distance from the shaded point to the occluder: full weight up close, fading to zero
		// at the radius. This is what confines occlusion to the contact crease instead of letting a nearby floor
		// darken the whole face — the old radius/dist form stayed ~0.5 even a radius or two away.
		float range = 1.0 - smoothstep(radius * 0.5, radius, length(p - surf.xyz));
		// clip.w is the (linear) view-space depth, so the in-front test and the bias are in world units — robust to
		// the sample radius. Comparing NDC z with a constant bias is not: at a small radius the NDC z gaps shrink
		// below the bias and every sample is rejected, which is what made the term vanish entirely.
		float surfDepth = mul(pc.viewProj, float4(surf.xyz, 1.0)).w;
		if (surfDepth < qClip.w - bias)               // occluder nearer the camera than the sample point → occluded
			occlusion += range;
	}

	return saturate(1.0 - occlusion / kKernel * intensity);
}
