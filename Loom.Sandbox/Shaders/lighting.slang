// Deferred lighting technique (Planning.md §6, M5.3 + §7.2 shadows). A fullscreen pass that samples the
// g-buffer (albedo 0, encoded normal 1, world position 2) and the sun's shadow map (3), and writes the lit
// result. Single directional key light + ambient, pushed into HDR so bloom has something to catch. The
// shadow test reuses the sun's view-projection (a fragment push constant, the same matrix the shadow pass
// drew with): project the world position into light space and compare against the stored shadow depth.
// Background pixels have albedo 0, so they stay black regardless of the (undefined) normal there.

[[vk::binding(0, 0)]] Sampler2D albedoTex;
[[vk::binding(1, 0)]] Sampler2D normalTex;
[[vk::binding(2, 0)]] Sampler2D positionTex;
[[vk::binding(3, 0)]] Sampler2D shadowMap;
[[vk::binding(4, 0)]] Sampler2D aoTex; // screen-space ambient occlusion (§7.6)

struct PushConstants
{
	float4x4 sunViewProj; // the sun's view-projection — world space → the shadow map's clip space
};

[[vk::push_constant]] PushConstants pc;

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

// 1.0 = fully lit, lower = in the sun's shadow. Projects the world position into light space and compares
// its depth against the nearest caster the shadow map recorded (with a slope-independent bias for acne).
float sunVisibility(float3 worldPos)
{
	float4 lightClip = mul(pc.sunViewProj, float4(worldPos, 1.0));
	float3 ndc = lightClip.xyz / lightClip.w;
	float2 uv = ndc.xy * 0.5 + 0.5; // clip [-1,1] → shadow-map uv [0,1] (Vulkan-convention clip space on both backends)

	if (any(uv < 0.0) || any(uv > 1.0) || ndc.z > 1.0)
		return 1.0; // outside the sun's frustum — treat as lit

	float nearest = shadowMap.Sample(uv).r;
	return ndc.z - 0.0025 > nearest ? 0.25 : 1.0; // occluded → keep only ambient
}

[shader("fragment")]
float4 fragmentMain(VsOut input) : SV_Target
{
	float3 albedo = albedoTex.Sample(input.uv).rgb;
	float3 normal = normalize(normalTex.Sample(input.uv).rgb * 2.0 - 1.0); // decode [0,1] -> [-1,1]
	float3 worldPos = positionTex.Sample(input.uv).rgb;

	float3 lightDir = normalize(float3(0.6, 0.55, 0.45)); // matches the sandbox sun; lower angle → long shadow
	float ndotl = max(dot(normal, lightDir), 0.0);
	float ambient = 0.15;

	// A key light strong enough to push the directly-lit faces into HDR (>1) for bloom; the shadow term
	// removes the key light (but not ambient) where the sun is occluded. lit target is Rgba16F to hold it.
	const float keyIntensity = 1.8;
	float shadow = sunVisibility(worldPos);
	float ao = aoTex.Sample(input.uv).r; // occludes only the ambient/indirect term
	return float4(albedo * (ambient * ao + ndotl * keyIntensity * shadow), 1.0);
}
