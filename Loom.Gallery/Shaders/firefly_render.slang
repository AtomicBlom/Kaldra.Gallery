// Firefly render — each mote (a float4: xyz world position + w brightness) becomes a small additive billboard with
// a soft warm yellow-green glow plus a wider halo, sized in clip space so it holds a roughly constant on-screen
// size with depth. The HDR brightness pushes it past the bloom threshold, so each mote reads as a glowing orb of
// light rather than a hard dot. The mote is already in world space, so the pass needs only the .Reads
// view-projection (no per-object model). A 6-vertex billboard per instance (no point sprites — Adreno is uneven).

#include "loom_platform.module.slang"

struct ViewProj { float4x4 viewProj; }; // the app's view-projection, clip-space-corrected
// No samplers precede it, so the UBO owns binding 0 (register b1; b0 is the unused root-constant slot).
LOOM_BINDING(0) ConstantBuffer<ViewProj> view LOOM_REGISTER(b1);

struct VsOut
{
	float4 position : SV_Position;
	float2 offset : TEXCOORD0; // quad-local [-1,1], for the round falloff
	float bright : TEXCOORD1;
};

[shader("vertex")]
VsOut vertexMain([[vk::location(0)]] float4 mote : POSITION, uint vertexId : SV_VertexID)
{
	const float2 corners[6] = {
		float2(-1, -1), float2(1, -1), float2(1, 1),
		float2(-1, -1), float2(1, 1), float2(-1, 1),
	};
	float2 corner = corners[vertexId];

	float4 clip = mul(view.viewProj, float4(mote.xyz, 1.0));
	const float size = 0.022; // clip-space → constant on-screen mote size
	clip.xy += corner * size * clip.w;

	VsOut output;
	output.position = clip;
	output.offset = corner;
	output.bright = mote.w;
	return output;
}

[shader("fragment")]
float4 fragmentMain(VsOut input) : SV_Target
{
	float d = length(input.offset);
	float core = saturate(1.0 - d);
	core *= core;                                  // tight bright core
	float halo = saturate(1.0 - d) * 0.35;         // soft surrounding glow
	float3 colour = float3(1.5, 1.9, 0.7) * input.bright * (core + halo); // warm yellow-green firefly (HDR → blooms)
	return float4(colour, 1.0);
}
