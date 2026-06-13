// G-buffer technique (Planning.md §6 deferred path, M5.2): the geometry pass writes several colour targets
// at once (multiple render targets) plus depth. Vertex transforms by the MVP push constant; the fragment
// outputs albedo to SV_Target0 and the encoded normal to SV_Target1, in the same order the pass declares
// .Writes(albedo, normal). A later fullscreen pass samples these to do deferred lighting.

struct PushConstants
{
	float4x4 mvp;
};

[[vk::push_constant]] PushConstants pc;

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

struct GBuffer
{
	float4 albedo : SV_Target0;
	float4 normal : SV_Target1;
};

[shader("vertex")]
VsOut vertexMain(VsIn input)
{
	VsOut output;
	output.position = mul(pc.mvp, float4(input.position, 1.0));
	output.normal = input.normal;
	output.uv = input.uv;
	return output;
}

[shader("fragment")]
GBuffer fragmentMain(VsOut input)
{
	GBuffer output;
	output.albedo = float4(0.82, 0.76, 0.70, 1.0);                      // a warm base colour
	output.normal = float4(normalize(input.normal) * 0.5 + 0.5, 1.0);  // [-1,1] -> [0,1] for an unorm/float target
	return output;
}
