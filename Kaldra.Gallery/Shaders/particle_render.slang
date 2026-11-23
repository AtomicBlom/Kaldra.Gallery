// GPU particle render. Each particle the compute wrote is one INSTANCE; the buffer is bound as the per-instance
// vertex stream (one float4: xyz position + w life). The vertex stage fabricates a 6-vertex quad per instance (a
// billboard — no point sprites, which Adreno handles unevenly) sized in clip space, and the fragment makes a soft
// round emissive spark, additively blended onto lit so it glows through bloom. The graph emitted the
// compute→vertex barrier; this just draws. The particle is already in world space, so it needs only the
// view-projection the pass .Reads (no per-object model).

#include "kaldra_platform.module.slang"

struct ViewProj { float4x4 viewProj; }; // the app's view-projection, clip-space-corrected
// No samplers precede it, so the UBO owns binding 0.
ConstantBuffer<ViewProj> view;

struct VsOut
{
	float4 position : SV_Position;
	float2 offset : TEXCOORD0; // quad-local [-1,1], for the round falloff
	float life : TEXCOORD1;
};

[shader("vertex")]
VsOut vertexMain([[vk::location(0)]] float4 particle : POSITION, uint vertexId : SV_VertexID)
{
	// Two triangles of a unit quad in local [-1,1].
	const float2 corners[6] = {
		float2(-1, -1), float2(1, -1), float2(1, 1),
		float2(-1, -1), float2(1, 1), float2(-1, 1),
	};
	float2 corner = corners[vertexId];

	float4 clip = mul(view.viewProj, float4(particle.xyz, 1.0));
	// Offset in clip space scaled by w, so the quad keeps a roughly constant on-screen size with depth.
	const float size = 0.012;
	clip.xy += corner * size * clip.w;

	VsOut output;
	output.position = clip;
	output.offset = corner;
	output.life = particle.w;
	return output;
}

[shader("fragment")]
float4 fragmentMain(VsOut input) : SV_Target
{
	float d = length(input.offset);
	float falloff = saturate(1.0 - d);
	falloff *= falloff;                            // soft round dot
	float3 colour = float3(2.6, 1.6, 0.5) * input.life * falloff; // emissive warm spark (HDR → blooms)
	return float4(colour, 1.0);
}
