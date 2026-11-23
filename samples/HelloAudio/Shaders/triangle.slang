// HelloTriangle spike: a hardcoded, vertex-ID-fabricated triangle — no vertex buffer, no camera, no material.
// The smallest possible technique that still exercises the real render-graph + backend pipeline end to end.
// Modeled on BinaryVibrance.Kaldra.Shaders/Shaders/fullscreen.slang's SV_VertexID fabrication trick.

#include "kaldra_platform.module.slang"

struct VsOut
{
	float4 position : SV_Position;
	float3 color : COLOR0;
};

[shader("vertex")]
VsOut vertexMain(uint vertexId : SV_VertexID)
{
	float2 positions[3] = { float2(0.0, 0.75), float2(-0.75, -0.75), float2(0.75, -0.75) };
	float3 colors[3] = { float3(1.0, 0.2, 0.2), float3(0.2, 1.0, 0.2), float3(0.2, 0.4, 1.0) };

	float2 pos = positions[vertexId];

	VsOut output;
	output.color = colors[vertexId];
	// Fabricated directly in clip space, so there is no camera carrying the backend's clip-space correction —
	// KALDRA_CLIP_POS applies the NDC-Y convention this target needs.
	output.position = KALDRA_CLIP_POS(pos);
	return output;
}

[shader("fragment")]
float4 fragmentMain(VsOut input) : SV_Target
{
	return float4(input.color, 1.0);
}
