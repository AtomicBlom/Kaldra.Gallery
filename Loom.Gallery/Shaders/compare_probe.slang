// Test support: a fullscreen pass that reads its one input by comparison (a shadow sampler) at the pixel's UV against a
// reference depth of 0.5, and writes the result as grey: white where the comparison passes, black where it fails. Over
// depth_ramp's target, the technique's SampleCompare decides which half of the frame is lit. Entry
// vertexMain/fragmentMain.

#include "loom_platform.module.slang"

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

Sampler2DShadow depthMap;

[shader("fragment")]
float4 fragmentMain(VsOut input) : SV_Target
{
	float passed = depthMap.SampleCmpLevelZero(input.uv, 0.5);
	return float4(passed, passed, passed, 1.0);
}
