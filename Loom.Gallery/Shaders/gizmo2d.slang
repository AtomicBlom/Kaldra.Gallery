// 2D light gizmo (PlanningPhase2.md M14-7, Track A — debug overlays). One additive ring per light, drawn over the
// lit target to visualise each light's position (centre marker) and reach (the ring at its radius). Reuses the
// Light2DInstance stream (SandboxVertexLayouts.Light2D) the real lights use — the same data, drawn as a wireframe instead
// of shaded — so the gizmo can't drift from what's actually lighting the scene. Procedural (no texture): the
// fragment derives the ring from the quad-local coordinate. Additive blend, so it glows over the scene without a
// depth/occlusion test. Entry points vertexMain/fragmentMain so the build emits DXIL.

#include "loom_platform.module.slang"

struct ViewProj
{
	float4x4 viewProj; // camera view-projection, clip-space-corrected — the app composes it; same view the lights read
};

// The view-proj UBO, the one uniform slot.
ConstantBuffer<ViewProj> view;

struct VsIn
{
	[[vk::location(0)]] float3 corner : POSITION;     // unit-quad corner (0,0)..(1,1)
	[[vk::location(1)]] float3 normal : NORMAL;       // unused
	[[vk::location(2)]] float2 quadUv : TEXCOORD0;    // unused
	[[vk::location(3)]] float4 tangent : TANGENT;     // unused
	[[vk::location(4)]] float4 iPosRadInt : ILIGHT0;  // position.xy, radius, intensity
	[[vk::location(5)]] float4 iColHeight : ILIGHT1;  // colour.rgb, height
};

struct VsOut
{
	float4 position : SV_Position;
	float2 local : TEXCOORD0;  // quad-local [-1,1] — 1.0 is the light's radius edge
	float3 color : TEXCOORD1;
};

[shader("vertex")]
VsOut vertexMain(VsIn input)
{
	float2 center = input.iPosRadInt.xy;
	float radius = input.iPosRadInt.z;

	float2 local = input.corner.xy * 2.0 - 1.0; // [-1,1] across the light's bounding box
	float3 world = float3(center + local * radius, 0.0);

	VsOut output;
	output.position = mul(view.viewProj, float4(world, 1.0));
	output.local = local;
	output.color = input.iColHeight.rgb;
	return output;
}

[shader("fragment")]
float4 fragmentMain(VsOut input) : SV_Target
{
	float d = length(input.local);                              // 0 at centre, 1 at the radius edge
	float ring = 1.0 - smoothstep(0.0, 0.045, abs(d - 1.0));    // bright band at the radius
	float dot = 1.0 - smoothstep(0.0, 0.10, d);                 // centre marker
	float gizmo = saturate(max(ring, dot));
	if (gizmo < 0.02)
		discard; // most of the quad is empty — skip it so additive blend stays clean

	return float4(input.color * gizmo, 1.0); // additive over the lit scene
}
