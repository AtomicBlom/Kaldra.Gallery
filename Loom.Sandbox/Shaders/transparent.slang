// Transparent forward technique (Planning.md §12 M5) — the .Blends counterpart to the opaque g-buffer
// pass. It draws the same cube geometry again, but instead of producing colour it *accumulates* a
// translucent tint onto the already-lit target (the pass .Blends(lit)). It .ReadsDepth(depth): the depth
// buffer the opaque pass wrote is bound read-only, so this draw is depth-tested against the solid cube
// (no z-fighting, no writes), and the alpha blend (src-alpha over) the backend enables for a Blend
// technique lays the tint over the lit surface. The vertex stage matches mesh.slang (MVP push constant,
// Loom.Core.Vertex layout at locations 0/1/2).

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
	output.uv = input.uv; // passed through so the vertex stage consumes the uv attribute (location 2)
	return output;
}

[shader("fragment")]
float4 fragmentMain(VsOut input) : SV_Target
{
	// A glassy blue tint; the alpha drives the over-blend (0.5 → half tint, half lit). A faint
	// normal-based facet keeps the translucency reading as a surface rather than a flat wash.
	float facet = normalize(input.normal).y * 0.15 + 0.85;
	return float4(float3(0.2, 0.45, 0.95) * facet, 0.5);
}
