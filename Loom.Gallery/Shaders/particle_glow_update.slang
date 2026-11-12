// Generalized GPU particle update (PlanningPhase2.md M14-4, Track A). Widens the old single-float4 fountain
// (particle_update.slang) into a 32-byte particle — position + remaining-life, and velocity + a per-particle
// seed — driven by a PARAMETERISED emitter passed as push constants (origin, spawn width, rise/drift speed,
// gravity, swirl). The demo owns the emitter shape; the shader is generic. Stateless + deterministic: each
// particle's age is a pure function of (index, time), integrated analytically as pos = spawn + v0·age +
// ½·g·age², so no cross-frame buffer state is needed and a frozen capture reproduces bit-for-bit (the
// cross-backend golden). The render pass reads this same buffer as the per-instance vertex stream
// (SandboxVertexLayouts.Particle, two IPARTICLE float4 rows); the graph derives the compute→vertex barrier.

#include "loom_platform.module.slang"
#include "hash.module.slang"

struct Particle
{
	float4 posLife; // xyz world position, w = remaining life (1 at spawn → 0 at death)
	float4 velSeed; // xyz current velocity, w = per-particle seed (stable across the life)
};

RWStructuredBuffer<Particle> particles;

struct PushConstants
{
	float4 p0; // x = time (s), y = lifetime (s), z = origin.x, w = origin.y
	float4 p1; // x = spawn half-width, y = rise speed, z = drift speed, w = gravity
	float4 p2; // x = swirl amplitude, y = swirl frequency, z = origin.z (depth), w = unused
};

LOOM_PUSH_CONSTANT PushConstants pc;

[shader("compute")]
[numthreads(64, 1, 1)]
void computeMain(uint3 id : SV_DispatchThreadID)
{
	uint i = id.x;
	uint count = 0;
	uint stride = 0;
	particles.GetDimensions(count, stride);
	if (i >= count)
		return;

	float time = pc.p0.x;
	float lifetime = max(pc.p0.y, 0.001);
	float2 origin = pc.p0.zw;
	float spawnHalfWidth = pc.p1.x;
	float rise = pc.p1.y;
	float drift = pc.p1.z;
	float gravity = pc.p1.w;
	float swirlAmp = pc.p2.x;
	float swirlFreq = pc.p2.y;
	float originZ = pc.p2.z;

	// Per-particle randoms, stable across the whole life (they depend only on the index).
	float seed = Hash01(i * 3u);
	float seedX = Hash01(i * 3u + 1u);
	float seedAng = Hash01(i * 3u + 2u);

	// Each particle loops on its own staggered phase → a continuous stream of births rather than a pulse.
	float phase = frac(time / lifetime + seed);
	float age = phase * lifetime;
	float life = 1.0 - phase;

	// Spawn across the emitter band; initial velocity rises with a little per-particle horizontal drift.
	float spawnX = origin.x + (seedX * 2.0 - 1.0) * spawnHalfWidth;
	float v0x = (seedX * 2.0 - 1.0) * drift;
	float v0y = rise * (0.7 + 0.6 * seed);

	// Analytic integration + a sideways swirl that grows with age (curls the rising embers).
	float swirl = sin(age * swirlFreq + seedAng * 6.2831853) * swirlAmp * phase;
	float x = spawnX + v0x * age + swirl;
	float y = origin.y + v0y * age + 0.5 * gravity * age * age;
	float vy = v0y + gravity * age;

	particles[i].posLife = float4(x, y, originZ, life);
	particles[i].velSeed = float4(v0x, vy, 0.0, seed);
}
