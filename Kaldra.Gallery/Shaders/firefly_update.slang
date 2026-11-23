// Firefly update — a handful of glowing motes that WANDER through a volume (not the rising fountain of
// particle_update.slang). Each mote's position is a pure function of (index, time): a scattered base point plus a
// slow per-axis lissajous drift at distinct frequencies, so the motes roam independently and a frozen capture
// reproduces bit-for-bit across backends. The .w channel carries a pulsing HDR brightness (the twinkle) the
// additive render multiplies. The render reads this buffer as the per-instance stream (one float4 — the
// ParticlePosition layout); the graph derives the compute->vertex barrier from the shared buffer.

#include "kaldra_platform.module.slang"
#include "hash.module.slang"

RWStructuredBuffer<float4> fireflies;

struct PushConstants { float4 p; }; // x = elapsed time (seconds)
KALDRA_PUSH_CONSTANT PushConstants pc;

[shader("compute")]
[numthreads(64, 1, 1)]
void computeMain(uint3 id : SV_DispatchThreadID)
{
	uint i = id.x;
	uint count = 0;
	uint stride = 0;
	fireflies.GetDimensions(count, stride);
	if (i >= count)
		return;

	float t = pc.p.x;

	// Scatter a base point across the glade from stable per-index randoms.
	float rx = Hash01(i * 3u);
	float ry = Hash01(i * 3u + 1u);
	float rz = Hash01(i * 3u + 2u);
	float3 base = float3((rx - 0.5) * 7.0, 0.5 + ry * 1.9, (rz - 0.5) * 7.0);

	// Slow wander: distinct per-axis frequencies + a per-mote phase so no two drift alike.
	float ph = rx * 6.2831853;
	float3 wander = float3(
		sin(t * 0.55 + ph) * 0.9,
		sin(t * 0.90 + ph * 1.7) * 0.4,
		cos(t * 0.48 + ph * 2.3) * 0.9);

	// Twinkle: a per-mote brightness pulse, kept well above 1 so each mote blooms into a glowing orb.
	float pulse = 1.8 + 0.9 * sin(t * 3.0 + ph * 4.0);
	fireflies[i] = float4(base + wander, pulse);
}
