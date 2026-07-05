// 2D G-buffer channel inspector (PlanningPhase2.md M14-7, Track A — debug overlays). A fullscreen pass that tiles
// the deferred 2D G-buffer into four quadrants so the whole pipeline is legible at a glance: albedo (what the
// sprites are), the encoded world normal as RGB (the classic normal view — the relief the lights read), the
// normal's facing as grayscale (n.z: flat surfaces white, edges dark), and the final lit result (with the light
// gizmos, which were blended into lit). Thin separators divide the quadrants. This is the in-graph counterpart to
// the engine's runtime resource viewer (M13), focused on the 2D deferred targets. Entry points vertexMain/fragmentMain.

#include "loom_platform.module.slang"

LOOM_BINDING(0) Sampler2D albedoTex; // 2D G-buffer albedo
LOOM_BINDING(1) Sampler2D normalTex; // 2D G-buffer encoded world normal
LOOM_BINDING(2) Sampler2D litTex;    // the final lit result (+ gizmos)

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
	float2 uv = input.uv;
	float2 cell = floor(uv * 2.0);   // (col, row); row 0 is the top (v=0 top)
	float2 inner = frac(uv * 2.0);   // within-quadrant uv [0,1]

	float3 col;
	if (cell.y < 0.5)
	{
		// Top row: albedo | normal-as-RGB.
		col = cell.x < 0.5 ? albedoTex.Sample(inner).rgb : normalTex.Sample(inner).rgb;
	}
	else if (cell.x < 0.5)
	{
		// Bottom-left: the normal's z (how much each pixel faces the camera) as grayscale.
		float3 n = normalTex.Sample(inner).rgb * 2.0 - 1.0;
		col = saturate(n.z).xxx;
	}
	else
	{
		// Bottom-right: the lit result (carries the light gizmos blended into lit).
		col = litTex.Sample(inner).rgb;
	}

	// Thin separators along the quadrant seams (uv == 0.5 in either axis).
	float2 seam = abs(uv - 0.5);
	if (seam.x < 0.0015 || seam.y < 0.0015)
		col = float3(0.15, 0.15, 0.18);

	return float4(col, 1.0);
}
