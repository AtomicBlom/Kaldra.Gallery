// Normal-mapped G-buffer technique (PlanningPhase2.md M11). Like gbuffer.slang it writes the deferred targets
// — albedo (SV_Target0), encoded world normal (SV_Target1), world position (SV_Target2) — so the existing
// deferred lighting (lighting.slang / light_shadow.slang) consumes it unchanged. The difference is the surface:
// albedo comes from a material texture (slot 0) and the per-pixel normal from a tangent-space normal map
// (slot 1), rotated into world space by the TBN frame built from the interpolated normal + vertex tangent.
// UVs are scaled by TILES so the demo's [0,1]-mapped geometry tiles the texture — which exercises the Repeat
// sampler address mode and the mip chain. Entry points are vertexMain/fragmentMain so the Loom build emits DXIL.

[[vk::binding(0, 0)]] Sampler2D albedoTex; // material slot 0 — base colour (sRGB)
[[vk::binding(1, 0)]] Sampler2D normalTex; // material slot 1 — tangent-space normal map (linear)

struct PushConstants
{
	float4x4 mvp;
};

[[vk::push_constant]] PushConstants pc;

static const float TILES = 3.0; // > 1 so [0,1] UVs tile — exercises Repeat addressing + mip sampling

struct VsIn
{
	[[vk::location(0)]] float3 position : POSITION;
	[[vk::location(1)]] float3 normal   : NORMAL;
	[[vk::location(2)]] float2 uv        : TEXCOORD0;
	[[vk::location(3)]] float4 tangent   : TANGENT;
};

struct VsOut
{
	float4 position : SV_Position;
	float3 normal : NORMAL;
	float4 tangent : TANGENT;
	float3 worldPos : POSITION0;
	float2 uv : TEXCOORD0;
};

struct GBuffer
{
	float4 albedo : SV_Target0;
	float4 normal : SV_Target1;
	float4 position : SV_Target2;
};

[shader("vertex")]
VsOut vertexMain(VsIn input)
{
	VsOut output;
	output.position = mul(pc.mvp, float4(input.position, 1.0));
	output.normal = input.normal;     // model is identity, so object space == world space
	output.tangent = input.tangent;
	output.worldPos = input.position;
	output.uv = input.uv;
	return output;
}

[shader("fragment")]
GBuffer fragmentMain(VsOut input)
{
	float2 uv = input.uv * TILES;
	float3 albedo = albedoTex.Sample(uv).rgb;
	float3 ts = normalTex.Sample(uv).rgb * 2.0 - 1.0; // decode tangent-space normal [0,1] -> [-1,1]

	// TBN: rebuild the bitangent from the glTF-convention handedness, rotate the tangent-space normal to world.
	float3 N = normalize(input.normal);
	float3 T = normalize(input.tangent.xyz);
	float3 B = cross(N, T) * input.tangent.w;
	float3 worldN = normalize(T * ts.x + B * ts.y + N * ts.z);

	GBuffer output;
	output.albedo = float4(albedo, 1.0);
	output.normal = float4(worldN * 0.5 + 0.5, 1.0); // encode [-1,1] -> [0,1] for the lighting pass to decode
	output.position = float4(input.worldPos, 1.0);
	return output;
}
