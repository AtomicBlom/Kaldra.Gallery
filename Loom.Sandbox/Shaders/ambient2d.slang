// 2D deferred base pass (PlanningPhase2.md M14-3, Track A). The flat term of the 2D deferred lighting model: a
// fullscreen pass that seeds the lit target with ambient·albedo + emissive, before the data-driven additive 2D
// light volumes (light2d.slang) accumulate onto it. Splitting the base out (here) from the lights (there) is the
// standard deferred structure — one .Writes pass clears+fills the lit buffer, then one .Blends pass adds each
// light. Output is HDR (the lit target is Rgba16F), so bloom/tonemap can follow. Background pixels were cleared
// to albedo 0 by the sprite G-buffer pass, so they stay black (no ambient floor on empty space).
// Entry points vertexMain/fragmentMain so the Loom build emits DXIL.

[[vk::binding(0, 0)]] Sampler2D albedoTex;
[[vk::binding(1, 0)]] Sampler2D emissiveTex;

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
	float3 albedo = albedoTex.Sample(input.uv).rgb;
	float3 emissive = emissiveTex.Sample(input.uv).rgb;

	const float ambient = 0.12; // a low fill so unlit relief reads, but the lights still dominate
	return float4(albedo * ambient + emissive, 1.0);
}
