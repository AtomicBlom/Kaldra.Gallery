// Color grading (M15-6) — a fullscreen post pass that grades the composited scene before the blit. A procedural
// lift/contrast/saturation + tint controlled by a small push block, which doubles as the "season" hook (warm +
// desaturated for autumn, cool for winter, …). (A 3-D LUT is a later refinement; the tint grade is enough to
// show the stylized colour direction and stays a plain fullscreen pass with one sampled input.)

#include "loom_platform.module.slang"

LOOM_BINDING(0) Sampler2D sceneTex;

struct PushConstants
{
	float4 tint;     // multiplicative tint .rgb; .w = grade strength (0 = passthrough)
	float4 controls; // contrast (.x), saturation (.y), _, _
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

[shader("fragment")]
float4 fragmentMain(VsOut input) : SV_Target
{
	float3 color = sceneTex.Sample(input.uv).rgb;

	// Saturation about luminance, contrast about mid-grey, then a tint multiply.
	float luma = dot(color, float3(0.299, 0.587, 0.114));
	float3 graded = lerp(float3(luma, luma, luma), color, pc.controls.y);
	graded = (graded - 0.5) * pc.controls.x + 0.5;
	graded *= pc.tint.rgb;

	return float4(lerp(color, max(graded, 0.0), pc.tint.w), 1.0);
}
