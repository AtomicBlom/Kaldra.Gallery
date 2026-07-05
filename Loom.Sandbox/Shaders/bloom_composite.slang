// Bloom composite (Planning.md §7.1) — the bloom sub-graph's output pass. Samples the original lit image
// (binding 0) and the fully-accumulated bloom mip0 (binding 1), and writes lit + intensity*bloom into a
// fresh "lit_bloomed" target. This is a plain .Writes (not a blend): it produces a new resource the rest of
// the frame consumes, so the bloom's memory can be aliased away afterwards. The composite intensity is
// supplied by BloomGraph through the fragment push channel (M7-A3).

#include "loom_platform.module.slang"

LOOM_BINDING(0) Sampler2D litTex;
LOOM_BINDING(1) Sampler2D bloomTex;

struct PushConstants
{
	float intensity; // how strongly the glow composites back; wired from BloomGraph.Bloom(intensity:) (§7.1)
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
	output.position = float4(uv * 2.0 - 1.0, 0.0, 1.0);
	return output;
}

[shader("fragment")]
float4 fragmentMain(VsOut input) : SV_Target
{
	float intensity = pc.intensity; // how strongly the glow adds back over the lit image

	float3 lit = litTex.Sample(input.uv).rgb;
	float3 bloom = bloomTex.Sample(input.uv).rgb;
	return float4(lit + bloom * intensity, 1.0);
}
