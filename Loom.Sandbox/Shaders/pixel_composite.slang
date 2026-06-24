// Composite a small 3D "boss" render — drawn into its own tiny low-res domain — into a rectangular region of the
// 2D pixel-art scene, with NEAREST filtering so the 3D geometry reads as deliberately chunky pixels that sit at
// home in the pixel-art aesthetic. This is the low-res-domain feature in a compositing role rather than as the
// whole-scene renderer. Samples the full-screen 2D scene (slot 0), the boss colour (slot 1) and the boss depth
// (slot 2, its own domain). The push gives the boss's destination rect in screen UV (u0,v0 .. u1,v1). Inside the
// rect, where the boss depth shows geometry (nearer than the cleared far plane) the boss colour replaces the
// scene; everywhere else the 2D scene shows through. The graph derives the cross-domain shader-read transitions.

[[vk::binding(0, 0)]] Sampler2D scene;
[[vk::binding(1, 0)]] Sampler2D bossColor;
[[vk::binding(2, 0)]] Sampler2D bossDepth;

struct PushConstants
{
	float4 rect; // boss destination rect in screen UV: x=u0, y=v0, z=u1, w=v1
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

[shader("fragment")]
float4 fragmentMain(VsOut input) : SV_Target
{
	float3 colour = scene.Sample(input.uv).rgb;

	if (input.uv.x >= pc.rect.x && input.uv.x <= pc.rect.z &&
		input.uv.y >= pc.rect.y && input.uv.y <= pc.rect.w)
	{
		float2 bossUv = (input.uv - pc.rect.xy) / (pc.rect.zw - pc.rect.xy);
		float depth = bossDepth.Sample(bossUv).r;
		if (depth < 0.999) // boss geometry covers this texel (cleared far = 1.0)
			colour = bossColor.Sample(bossUv).rgb;
	}

	return float4(colour, 1.0);
}
