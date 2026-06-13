// Deferred lighting technique (Planning.md §6, M5.3). A fullscreen pass that samples the g-buffer (albedo
// at binding 0, encoded normal at binding 1) and writes the lit result. Simple single directional light with
// an ambient term — enough to prove the deferred path composes; richer lighting (many lights, shadows) layers
// on later. Background pixels have albedo 0, so they stay black regardless of the (undefined) normal there.

[[vk::binding(0, 0)]] Sampler2D albedoTex;
[[vk::binding(1, 0)]] Sampler2D normalTex;

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
	float3 normal = normalize(normalTex.Sample(input.uv).rgb * 2.0 - 1.0); // decode [0,1] -> [-1,1]

	float3 lightDir = normalize(float3(0.4, 0.8, 0.5));
	float ndotl = max(dot(normal, lightDir), 0.0);
	float ambient = 0.15;

	return float4(albedo * (ambient + ndotl), 1.0);
}
