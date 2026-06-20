// Day/night sky (M15-4). A fullscreen pass that fills the BACKGROUND (pixels with no geometry) of the lit
// target with an analytic gradient sky + a sun glow, driven by the time-of-day colours the host computes from
// the sun's elevation. Composited with alpha-over (.Blends): it outputs alpha 0 over geometry (keeping the lit
// pixel) and alpha 1 over background. The per-pixel view ray is reconstructed from the inverse view-projection,
// so the gradient follows the real horizon as the camera pitches.

[[vk::binding(0, 0)]] Sampler2D depthTex; // scene depth; == 1.0 (cleared far) = background (no geometry drawn)

struct SkyParams
{
	float4x4 invViewProj; // clip → world (inverse of the flipped camera view-proj), for the per-pixel ray
	float4 eye;           // camera world position (.xyz)
	float4 sunDir;        // direction toward the sun (.xyz)
	float4 zenith;        // sky colour overhead (.rgb)
	float4 horizon;       // sky colour at the horizon (.rgb)
	float4 sunColor;      // sun disc/glow colour (.rgb), glow sharpness (.w)
};
[[vk::binding(1, 0)]] ConstantBuffer<SkyParams> sky : register(b1);

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
	if (depthTex.Sample(input.uv).r < 0.99999)
		return float4(0.0, 0.0, 0.0, 0.0); // geometry (depth < cleared far) — leave the lit pixel (alpha-over keeps dst)

	// Reconstruct the world-space view ray for this pixel (mul with the cbuffer matrix = clip·invVP, row-vector).
	float4 worldH = mul(sky.invViewProj, float4(input.uv * 2.0 - 1.0, 1.0, 1.0));
	float3 rayDir = normalize(worldH.xyz / worldH.w - sky.eye.xyz);

	// Vertical gradient (horizon → zenith) + a soft sun glow toward the sun direction.
	float h = saturate(rayDir.y);
	float3 color = lerp(sky.horizon.rgb, sky.zenith.rgb, pow(h, 0.45));
	float glow = pow(max(dot(rayDir, normalize(sky.sunDir.xyz)), 0.0), sky.sunColor.w);
	color += sky.sunColor.rgb * glow;
	return float4(color, 1.0);
}
