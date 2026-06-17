// Object-id / picking pass (Planning.md §7.4). Draws opaque geometry writing each object's id into a
// single-channel target — the seed source the JFA outline grows a border from (§7.5: "same buffer, new
// consumer"). For the reference frame there is one object (the cube), so it writes a constant id of 1; with
// many objects the id would be per-instance data. Occlusion would normally reuse the opaque depth, but a
// single convex object is its own silhouette, so no depth test is needed here. Vertex stage matches the
// mesh/g-buffer techniques (MVP push constant, Loom.Core.Vertex layout).

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
float fragmentMain(VsOut input) : SV_Target
{
	return 1.0; // the cube's object id; background stays 0 (the target is cleared)
}
