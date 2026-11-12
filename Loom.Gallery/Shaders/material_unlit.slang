// An unlit tinted-geometry technique: the whole surface is one colour, straight from the per-material parameter block.
// Useful on its own (flat-shaded gizmos, silhouettes, debug proxies), and it pins one property no other shader here
// does — it declares `loomMaterial` **first**, before the view and per-draw blocks.
//
// That ordering is the point. In every other material shader the block is declared last, so binding it "at the slot
// reflection reported" and binding it "after everything else" happen to be the same thing, and a backend that quietly
// appended it would still render correctly. Here the block is uniform slot 0, so appending it would land it at slot 2
// and the shader would read a view-projection as its tint. Nothing about the declaration order is special-cased: the
// engine binds the block wherever reflection says it is.

#include "loom_platform.module.slang"

#include "transform.module.slang"

// 16 bytes — a single float4, deliberately a different size from material_pbr's 48-byte block, so the staged length
// comes from this shader's own reflection rather than a shared assumption.
struct LoomMaterial { float4 tint; };

struct ViewProj { float4x4 viewProj; };
struct PerDraw { float4x4 model; uint objectId; };

// The material block first, so its uniform slot is 0.
ConstantBuffer<LoomMaterial> loomMaterial;
ConstantBuffer<ViewProj> view;
ConstantBuffer<PerDraw> draw;

struct VsIn
{
	[[vk::location(0)]] float3 position : POSITION;
	[[vk::location(1)]] float3 normal : NORMAL;
	[[vk::location(2)]] float2 uv : TEXCOORD0;
	[[vk::location(3)]] float4 tangent : TANGENT;
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
	return loomMaterial.tint;
}
