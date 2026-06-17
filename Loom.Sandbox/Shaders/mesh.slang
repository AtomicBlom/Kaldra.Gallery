// Geometry technique (Planning.md §12 M5) — the counterpart to the fullscreen techniques: it reads a
// real vertex buffer instead of fabricating verts from SV_VertexID. The vertex layout mirrors
// Loom.Core.Vertex (position, normal, uv) and binds by explicit location (0/1/2), matching the backend's
// VertexInputAttributeDescription. Object-space positions are transformed by the MVP push constant
// (Loom.Core.Camera.ViewProjection × model); the fragment shades by world-ish normal so faces read as 3D.

struct PushConstants
{
	float4x4 mvp;
};

[[vk::push_constant]] PushConstants pc;

struct VsIn
{
	// HLSL semantics alongside the vk::location qualifiers (see lit.slang): Slang pins the SPIR-V input
	// locations from vk::location (Vulkan unchanged) AND emits the semantics for the HLSL/DXIL target (DX12).
	[[vk::location(0)]] float3 position : POSITION;
	[[vk::location(1)]] float3 normal   : NORMAL;
	[[vk::location(2)]] float2 uv        : TEXCOORD0;
	[[vk::location(3)]] float4 tangent   : TANGENT;
};

struct VsOut
{
	float4 position : SV_Position;
	float4 tangent : TANGENT; // consumed only to satisfy the shared 4-attribute vertex layout (M12 retires this)
	float3 normal : NORMAL;
	float2 uv : TEXCOORD0;
};

[shader("vertex")]
VsOut vertexMain(VsIn input)
{
	VsOut output;
	output.position = mul(pc.mvp, float4(input.position, 1.0));
	output.tangent = input.tangent;
	output.normal = input.normal;
	output.uv = input.uv;
	return output;
}

[shader("fragment")]
float4 fragmentMain(VsOut input) : SV_Target
{
	// Map the normal into [0,1] so each cube face gets a distinct, recognisable colour.
	float3 colour = normalize(input.normal) * 0.5 + 0.5;
	return float4(colour, 1.0);
}
