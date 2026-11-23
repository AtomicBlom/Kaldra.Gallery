// Test support: a fullscreen pass that writes depth only, rising with the horizontal UV (0 at the left edge, 1 at the
// right), so a depth target holds known values for a comparison sample to be checked against. Entry
// vertexMain/fragmentMain.

#include "kaldra_platform.module.slang"

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

[shader("fragment")]
float fragmentMain(VsOut input) : SV_Depth
{
	return input.uv.x;
}
