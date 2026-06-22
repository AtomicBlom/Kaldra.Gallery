// Reads a colour array from an app-writable storage buffer (an SSBO / read-only StructuredBuffer) the pass .Reads,
// and paints each screen quadrant with a different element. The output depends on the buffer's contents at distinct
// indices, so a correct frame proves the host-written array reached the shader (Vulkan storage buffer at binding 0;
// DirectX12 read-only root SRV at t0 — no samplers, so the SSBO is the first bound resource on each backend).

struct Color
{
	float4 rgba;
};

[[vk::binding(0, 0)]] StructuredBuffer<Color> palette : register(t0);

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
	// Quadrant → palette index: TL=0, TR=1, BL=2, BR=3.
	int index = (input.uv.x > 0.5 ? 1 : 0) + (input.uv.y > 0.5 ? 2 : 0);
	return palette[index].rgba;
}
