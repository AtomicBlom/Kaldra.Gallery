// Toon / cel deferred lighting (M15-6) — a sibling of lighting_csm: same g-buffer + cascade inputs and the same
// M15-0 uniform block, but the diffuse term is quantized into a few flat bands and a rim term is added for the
// cozy stylized look. Drop-in replacement for lighting_csm (the demo just swaps the technique name).

#include "loom_platform.module.slang"

static const int CASCADE_COUNT = 3;

LOOM_BINDING(0) Sampler2D albedoTex;
LOOM_BINDING(1) Sampler2D normalTex;
LOOM_BINDING(2) Sampler2D positionTex;
LOOM_BINDING(3) Sampler2D cascade0;
LOOM_BINDING(4) Sampler2D cascade1;
LOOM_BINDING(5) Sampler2D cascade2;

// The cascade split far-depths (.xyz) + cascade-tint flag (.w), an app-written parameter buffer the pass .Reads;
// bound at uniform slot 0 (host data), ahead of the in-closure matrix push.
struct CsmSplits
{
	float4 splits;
};
LOOM_BINDING(6) ConstantBuffer<CsmSplits> csmSplits : register(b1);

struct CsmParams
{
	float4x4 cascadeViewProj[CASCADE_COUNT];
	float4 eye;          // camera world position .xyz
	float4 forward;      // camera forward .xyz
	float4 sunDir;       // direction toward the sun .xyz, key intensity .w
	float4 sunColor;     // sun light colour .rgb
	float4 ambientColor; // ambient colour .rgb
};
LOOM_BINDING(7) ConstantBuffer<CsmParams> csm : register(b2);

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

float sampleCascade(int cascade, float2 uv)
{
	if (cascade == 0)
		return cascade0.Sample(uv).r;
	if (cascade == 1)
		return cascade1.Sample(uv).r;
	return cascade2.Sample(uv).r;
}

float sunVisibility(float3 worldPos, int cascade)
{
	float4 lightClip = mul(csm.cascadeViewProj[cascade], float4(worldPos, 1.0));
	float3 ndc = lightClip.xyz / lightClip.w;
	float2 uv = ndc.xy * 0.5 + 0.5;
	if (any(uv < 0.0) || any(uv > 1.0) || ndc.z > 1.0)
		return 1.0;
	float bias = 0.0015 * (cascade + 1);
	return ndc.z - bias > sampleCascade(cascade, uv) ? 0.0 : 1.0; // hard (banded) shadow
}

[shader("fragment")]
float4 fragmentMain(VsOut input) : SV_Target
{
	float3 albedo = albedoTex.Sample(input.uv).rgb;
	float3 normal = normalize(normalTex.Sample(input.uv).rgb * 2.0 - 1.0);
	float3 worldPos = positionTex.Sample(input.uv).rgb;

	float viewDepth = dot(worldPos - csm.eye.xyz, csm.forward.xyz);
	int cascade = 0;
	if (viewDepth > csmSplits.splits.x)
		cascade = 1;
	if (viewDepth > csmSplits.splits.y)
		cascade = 2;

	float3 lightDir = normalize(csm.sunDir.xyz);
	float ndotl = max(dot(normal, lightDir), 0.0);
	float shadow = sunVisibility(worldPos, cascade);

	// Cel banding: quantize the lit half-Lambert into 3 flat steps with a soft edge between them.
	float lit = ndotl * shadow;
	float banded = floor(lit * 3.0) / 3.0;
	banded += smoothstep(0.0, 0.04, frac(lit * 3.0)) / 3.0 * step(0.001, frac(lit * 3.0));
	banded = saturate(banded);

	// Rim light (view-grazing) for a soft cartoon edge.
	float3 viewDir = normalize(csm.eye.xyz - worldPos);
	float rim = pow(1.0 - max(dot(viewDir, normal), 0.0), 4.0) * 0.4 * step(0.0, ndotl);

	float3 color = albedo * (csm.ambientColor.rgb + csm.sunColor.rgb * banded * csm.sunDir.w) + csm.sunColor.rgb * rim;

	if (csmSplits.splits.w > 0.5)
	{
		float3 tint = cascade == 0 ? float3(1.0, 0.4, 0.4) : cascade == 1 ? float3(0.4, 1.0, 0.4) : float3(0.4, 0.4, 1.0);
		color *= tint;
	}
	return float4(color, 1.0);
}
