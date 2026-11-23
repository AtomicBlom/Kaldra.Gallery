// The Particles demo's fountain (particle_update.slang's), its shape read from a Constants block the compute pass
// .Reads, as a draw reads one: written once by the demo, while the time, which changes every frame, rides the push.
// The block is the compute's one uniform slot.

#include "kaldra_platform.module.slang"

RWStructuredBuffer<float4> particles;

struct Fountain
{
	float4 shape;   // x: how fast a particle loops (per second); y: the swirl (radians per second); z: the spread at
	                // its base; w: how much it widens as it rises
	float4 rise;    // x: the height it starts at; y: how high it rises
};
ConstantBuffer<Fountain> fountain;

struct PushConstants
{
	float4 p; // x = elapsed time (seconds)
};

KALDRA_PUSH_CONSTANT PushConstants pc;

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

	float time = pc.p.x;
	float phase = frac(time * fountain.shape.x + i * (1.0 / count));
	float angle = i * 2.39996323 + time * fountain.shape.y;
	float spread = fountain.shape.z + phase * fountain.shape.w;
	float height = fountain.rise.x + phase * fountain.rise.y;

	float3 pos = float3(cos(angle) * spread, height, sin(angle) * spread);
	particles[i] = float4(pos, 1.0 - phase);
}
