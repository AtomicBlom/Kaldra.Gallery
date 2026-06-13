// Geometry technique (Planning.md §12 M5) — the counterpart to the fullscreen techniques: it reads a
// real vertex buffer instead of fabricating verts from SV_VertexID. The vertex layout mirrors
// Loom.Core.Vertex (position, normal, uv) and binds by explicit location (0/1/2), matching the backend's
// VertexInputAttributeDescription. M5.1a draws positions already in clip space (no camera yet); the
// push-constant MVP arrives in 1b.

struct VsIn
{
	[[vk::location(0)]] float3 position;
	[[vk::location(1)]] float3 normal;
	[[vk::location(2)]] float2 uv;
};

struct VsOut
{
	float4 position : SV_Position;
	float3 normal : NORMAL;
	float2 uv : TEXCOORD0;
};

[shader("vertex")]
VsOut vertexMain(VsIn input)
{
	VsOut output;
	output.position = float4(input.position, 1.0);
	output.normal = input.normal;
	output.uv = input.uv;
	return output;
}

[shader("fragment")]
float4 fragmentMain(VsOut input) : SV_Target
{
	// Colour by UV so a successful indexed draw is unmistakable (matches the fullscreen gradient look).
	return float4(input.uv, 0.5, 1.0);
}
