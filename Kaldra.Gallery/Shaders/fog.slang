// Distance fog — a fullscreen post pass that fades lit geometry toward a fog colour by its distance from the
// camera, so a ground plane recedes into the haze instead of ending at a hard quad edge. Reads the HDR lit scene
// (slot 0) and the world-position G-buffer (slot 1); background texels (no geometry, position.a == 0) pass
// through untouched, since the sky pass fills them. The eye, fog start distance, colour and inverse fade range
// arrive as a push block.

#include "kaldra_platform.module.slang"

Sampler2D litTex;
Sampler2D positionTex;

struct PushConstants
{
	float4 eyeStart;     // camera world position .xyz, fog start distance .w
	float4 colorInvSpan; // fog colour .rgb, 1 / (fadeEnd - fadeStart) .w
};
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
	float3 lit = litTex.Sample(input.uv).rgb;
	float4 pos = positionTex.Sample(input.uv);
	if (pos.a < 0.5)
		return float4(lit, 1.0); // background — the sky pass owns these texels

	float dist = length(pos.xyz - pc.eyeStart.xyz);
	float f = saturate((dist - pc.eyeStart.w) * pc.colorInvSpan.w);
	return float4(lerp(lit, pc.colorInvSpan.rgb, f), 1.0);
}
