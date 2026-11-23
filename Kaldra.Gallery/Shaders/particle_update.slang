// GPU particle update (Planning.md §7.6). A COMPUTE pass that writes a storage BUFFER — one float4 per
// particle (xyz position + w = remaining life for the fade). A draw then reads the same buffer as vertex
// data; the engine derives the compute→vertex-input barrier from those edges. Positions are a pure function
// of (index, time) — a looping fountain — so no cross-frame state is needed; the buffer is rewritten each
// frame. RWStructuredBuffer is the storage-buffer descriptor (binding 0: no samplers, no storage images).

#include "kaldra_platform.module.slang"

RWStructuredBuffer<float4> particles;

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
	// Each particle loops on its own phase; staggering by index keeps the fountain continuous.
	float phase = frac(time * 0.22 + i * (1.0 / count));
	float angle = i * 2.39996323 + time * 0.6;     // golden-angle spread + slow swirl
	float spread = 0.15 + phase * 0.9;             // widens as it rises
	float height = -0.45 + phase * 3.0;            // rises from the cube base

	float3 pos = float3(cos(angle) * spread, height, sin(angle) * spread);
	particles[i] = float4(pos, 1.0 - phase);       // w: 1 at spawn → 0 at death (fades the dot)
}
