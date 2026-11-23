// Geometry technique — the counterpart to the fullscreen techniques: it reads a real vertex buffer instead of
// fabricating verts from SV_VertexID. The vertex layout mirrors BinaryVibrance.Kaldra.Core.Vertex (position, normal, uv, tangent)
// and binds by explicit location, matching the backend's VertexInputAttributeDescription. Object-space positions
// are transformed by the view-projection the pass .Reads and the per-object model; the fragment shades by the
// world normal so faces read as 3D.

#include "kaldra_platform.module.slang"

#include "transform.module.slang"

struct ViewProj { float4x4 viewProj; };              // the app's view-projection, clip-space-corrected
struct PerDraw  { float4x4 model; uint objectId; };  // per-object model + picking id (id unused here)

// No samplers precede them, so the UBOs own bindings 0,1.
ConstantBuffer<ViewProj> view;
ConstantBuffer<PerDraw>  draw;

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
	float4 tangent : TANGENT; // consumed only to satisfy the shared 4-attribute vertex layout
	float3 normal : NORMAL;
	float2 uv : TEXCOORD0;
};

[shader("vertex")]
VsOut vertexMain(VsIn input)
{
	VsOut output;
	float4 world = mul(draw.model, float4(input.position, 1.0));
	output.position = mul(view.viewProj, world);
	output.tangent = input.tangent;
	output.normal = mul(input.normal, inverse3x3((float3x3)draw.model));
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
