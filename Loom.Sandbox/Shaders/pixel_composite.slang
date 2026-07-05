// Composite the 2D pixel-art scene (its own low-res domain) + a small 3D "boss" render (its own tiny domain) onto
// the full backbuffer, with NEAREST filtering so both read as deliberately chunky pixels. The 2D scene is fit to
// the screen with a preserved aspect ratio (letterbox/pillarbox bars), so the pixels stay uniformly scaled on any
// window shape rather than stretching. Inside the boss rect (given in CONTENT uv) the boss colour replaces the
// scene where the boss depth shows geometry (nearer than the cleared far plane). The graph derives the cross-domain
// shader-read transitions.

#include "loom_platform.module.slang"

LOOM_BINDING(0) Sampler2D scene;     // the 2D pixel-art scene (low-res domain)
LOOM_BINDING(1) Sampler2D bossColor; // the boss render (tiny domain)
LOOM_BINDING(2) Sampler2D bossDepth; // the boss depth (its domain)

struct PushConstants
{
	float4 rect;   // boss destination rect in CONTENT uv: x=u0, y=v0, z=u1, w=v1
	float4 aspect; // x = screen aspect (w/h), y = content aspect (w/h)
};
LOOM_PUSH_CONSTANT PushConstants pc;

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
	// Fit the content's aspect inside the screen, centred — bars fill the rest, so pixels never stretch.
	float2 scale = float2(1.0, 1.0);
	if (pc.aspect.x > pc.aspect.y)
		scale.x = pc.aspect.y / pc.aspect.x; // wider screen → pillarbox (bars left/right)
	else
		scale.y = pc.aspect.x / pc.aspect.y; // taller screen → letterbox (bars top/bottom)
	float2 lo = (1.0 - scale) * 0.5;
	float2 cuv = (input.uv - lo) / scale; // screen uv → content uv

	if (any(cuv < 0.0) || any(cuv > 1.0))
		return float4(0.0, 0.0, 0.0, 1.0); // the bars

	float3 colour = scene.Sample(cuv).rgb;

	if (cuv.x >= pc.rect.x && cuv.x <= pc.rect.z && cuv.y >= pc.rect.y && cuv.y <= pc.rect.w)
	{
		float2 bossUv = (cuv - pc.rect.xy) / (pc.rect.zw - pc.rect.xy);
		if (bossDepth.Sample(bossUv).r < 0.999) // boss geometry covers this texel (cleared far = 1.0)
			colour = bossColor.Sample(bossUv).rgb;
	}

	return float4(colour, 1.0);
}
