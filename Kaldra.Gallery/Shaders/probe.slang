// Test/probe support: a fullscreen pass that emits a pushed RGBA colour scaled by the horizontal UV, so the frame
// is a 0→colour gradient. A controlled input with no shading math to confound a measurement — the targeted blend
// test combines two of these under a blend mode, and the tone-map property test feeds an HDR (>1) gradient through
// the exposure + tone-map stages. Alpha passes through unscaled for the alpha-over blend. Entry vertexMain/fragmentMain.

#include "kaldra_platform.module.slang"

struct PushConstants { float4 color; }; // .rgb may be HDR (>1); .a drives the alpha-over blend
KALDRA_PUSH_CONSTANT PushConstants pc;

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
float4 fragmentMain(VsOut input) : SV_Target
{
	return float4(pc.color.rgb * input.uv.x, pc.color.a);
}
