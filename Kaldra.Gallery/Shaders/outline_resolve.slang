// Outline resolve (Planning.md §7.5). The final JFA buffer holds, per pixel, the coordinate of the nearest
// hovered-object pixel; the distance to it is one subtraction. A thin band of that distance, just OUTSIDE the
// silhouette, is the border ring; everywhere else the lit image passes through unchanged. The ring colour is
// emissive (HDR) and composited ADDITIVELY, so when this result is fed through bloom (§7.5: "feed `outlined`
// into bloom and the hover border glows") the ring blooms into a soft halo hugging the cube. With nothing
// hovered the seed buffer is all sentinel, distances are huge, and no ring appears.

#include "kaldra_platform.module.slang"

Sampler2D seed; // final flood: nearest-seed coordinates
Sampler2D litTex;

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
	output.position = KALDRA_FULLSCREEN_POS(uv);
	return output;
}

[shader("fragment")]
float4 fragmentMain(VsOut input) : SV_Target
{
	static const float width = 2.5;                        // ring half-width, in world pixels
	static const float3 ringColour = float3(0.25, 2.2, 3.0); // emissive cyan (HDR) so bloom catches it

	float3 lit = litTex.Sample(input.uv).rgb;

	float2 here = input.position.xy;
	float2 nearest = seed.Sample(input.uv).xy;
	float d = distance(here, nearest); // pixels to the nearest hovered-object pixel (0 inside the object)

	// Outer ring: a band just beyond the silhouette (d in ~0.5..width). step(0.5,d) drops the interior.
	float ring = smoothstep(width, width - 1.5, d) * step(0.5, d);

	return float4(lit + ring * ringColour, 1.0);
}
