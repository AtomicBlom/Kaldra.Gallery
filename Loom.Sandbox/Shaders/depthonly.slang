// Depth-only technique (Planning.md §7.2 shadows). Drawn from the sun's view (the view: param) into the
// shadow map: it writes only depth — no colour target, no fragment output — so each shadow-map texel holds
// the distance from the sun to the nearest caster. Deferred lighting then compares against it. The vertex
// stage is the usual MVP transform; here the MVP is the sun's view-projection, supplied via ctx.Draw(view:).

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
	// Passed through (unused by the fragment) only so the vertex stage consumes the shared mesh layout's
	// normal/uv attributes — keeps validation quiet without a per-technique vertex-input layout.
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
void fragmentMain(VsOut input)
{
	// No colour output — depth is written by the fixed-function depth test/write alone.
}
