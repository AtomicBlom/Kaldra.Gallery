// Generalized GPU particle render (PlanningPhase2.md M14-4, Track A) — the TEXTURED, colour-over-life half of
// the generalized particle system, and the one demo path that exercises the M14-0 textured DrawParticles verb.
// Each particle the compute wrote is one INSTANCE of a billboard quad fabricated from SV_VertexID; the instance
// stream is Loom.Core VertexLayout.Particle (binding 0, two IPARTICLE float4 rows). The quad is expanded in
// WORLD space (ortho-correct: size is in world px) about the particle's position. The fragment samples a 4-frame
// spark atlas at a SUB-UV frame chosen from normalised life (a young spark is a tight bright core; an old one a
// soft faint puff) and tints it by a warm ember colour-over-life ramp. Additive blend (.Blends, BlendMode.Additive)
// glows it over the lit scene with HDR intensity > 1 so it feeds bloom. Entry points vertexMain/fragmentMain → DXIL.

[[vk::binding(0, 0)]] Sampler2D atlas; // 4×1 spark sub-UV atlas (slot 0)

struct PushConstants
{
	float4x4 mvp; // camera view-projection (the instanced/particle path's only push, vertex stage)
};

[[vk::push_constant]] PushConstants pc;

struct VsIn
{
	[[vk::location(0)]] float4 posLife : IPARTICLE0; // xyz world position, w = remaining life
	[[vk::location(1)]] float4 velSeed : IPARTICLE1; // xyz velocity, w = seed
};

struct VsOut
{
	float4 position : SV_Position;
	float2 uv : TEXCOORD0; // sub-UV into the chosen atlas frame
	float life : TEXCOORD1;
};

static const int FrameCount = 4;       // 4×1 atlas
static const float AtlasWidth = 128.0; // 4 × 32 px
static const float AtlasHeight = 32.0;
static const float HalfExtent = 3.5;   // billboard half-size, world px (the 320×180 ortho world)

[shader("vertex")]
VsOut vertexMain(VsIn input, uint vertexId : SV_VertexID)
{
	// Two triangles of a unit quad in [0,1] (corner doubles as the within-frame uv weight).
	const float2 corners[6] = {
		float2(0, 0), float2(1, 0), float2(1, 1),
		float2(0, 0), float2(1, 1), float2(0, 1),
	};
	float2 corner = corners[vertexId];

	float life = saturate(input.posLife.w);
	// Sparks shrink as they die (full at birth → 0.45× at death).
	float scale = HalfExtent * (0.45 + 0.55 * life);
	float2 local = (corner * 2.0 - 1.0) * scale;
	float3 world = float3(input.posLife.xy + local, input.posLife.z);

	// Sub-UV: the frame advances with age (1-life); a half-texel inset stops linear filtering bleeding neighbours.
	int frame = clamp((int)((1.0 - life) * FrameCount), 0, FrameCount - 1);
	float insetU = 0.5 / AtlasWidth;
	float insetV = 0.5 / AtlasHeight;
	float u = (float(frame) + insetU + corner.x * (1.0 - 2.0 * insetU)) / float(FrameCount);
	float v = insetV + corner.y * (1.0 - 2.0 * insetV);

	VsOut output;
	output.position = mul(pc.mvp, float4(world, 1.0));
	output.uv = float2(u, v);
	output.life = life;
	return output;
}

[shader("fragment")]
float4 fragmentMain(VsOut input) : SV_Target
{
	float spark = atlas.Sample(input.uv).r; // white radial falloff stored in the atlas
	float life = saturate(input.life);

	// Warm ember ramp: hot yellow-white at birth → deep red at death.
	float3 ember = lerp(float3(0.85, 0.18, 0.05), float3(1.0, 0.85, 0.55), life);

	// Quick fade-in over the first slice of life, then fade out as it dies; HDR scale feeds bloom.
	float fadeIn = saturate((1.0 - life) / 0.08);
	float bright = life * fadeIn * 2.4;

	float3 colour = ember * spark * bright;
	return float4(colour, 1.0); // additive: rgb carries the glow, alpha unused
}
