// 2D deferred point light — instanced additive light volumes (PlanningPhase2.md M14-3, Track A). Each instance
// is one light: the vertex shader expands a unit quad to the light's bounding box (centre ± radius), and the
// fragment samples the 2D G-buffer normal under each covered pixel and accumulates colour·intensity·N·L·atten
// additively into the lit target (.Blends, BlendMode.Additive). One instanced draw covers an unbounded, fully
// dynamic light count; overdraw is bounded by where the lights actually reach. The per-instance Light2DInstance
// arrives as two ILIGHT rows (ILIGHT0..1) — one repeated semantic with an index, like ISPRITE/ITRANSFORM, so
// DX12's semantic-name-plus-index binding picks them up. Entry points vertexMain/fragmentMain so the build emits DXIL.

[[vk::binding(0, 0)]] Sampler2D albedoTex; // 2D G-buffer albedo (slot 0)
[[vk::binding(1, 0)]] Sampler2D normalTex; // 2D G-buffer encoded world normal (slot 1)

struct PushConstants
{
	float4x4 mvp; // camera view-projection (the instanced path's only push, vertex stage) — same as the sprite pass
};

[[vk::push_constant]] PushConstants pc;

struct VsIn
{
	// Per-vertex (binding 0 — VertexLayout.MeshStandard), locations 0..3; only the corner is read (the unit quad).
	[[vk::location(0)]] float3 corner : POSITION;     // unit-quad corner (0,0)..(1,1)
	[[vk::location(1)]] float3 normal : NORMAL;       // unused
	[[vk::location(2)]] float2 quadUv : TEXCOORD0;    // unused
	[[vk::location(3)]] float4 tangent : TANGENT;     // unused
	// Per-instance Light2DInstance (binding 1 — SandboxVertexLayouts.Light2D), locations 4..5:
	[[vk::location(4)]] float4 iPosRadInt : ILIGHT0;  // position.xy, radius, intensity
	[[vk::location(5)]] float4 iColHeight : ILIGHT1;  // colour.rgb, height
};

struct VsOut
{
	float4 position : SV_Position;
	float4 clip : TEXCOORD0;          // clip-space copy — for the gradient-based G-buffer uv reconstruction
	float2 worldXY : TEXCOORD1;       // this fragment's world position (interpolated) — drives attenuation
	float4 lightPosRadInt : TEXCOORD2; // per-instance light params (constant across the quad)
	float4 lightColHeight : TEXCOORD3;
};

[shader("vertex")]
VsOut vertexMain(VsIn input)
{
	float2 center = input.iPosRadInt.xy;
	float radius = input.iPosRadInt.z;

	// Expand the unit corner [0,1] to [-1,1]·radius about the light centre — the quad spans the light's reach.
	float2 local = (input.corner.xy * 2.0 - 1.0) * radius;
	float3 world = float3(center + local, 0.0);

	VsOut output;
	output.position = mul(pc.mvp, float4(world, 1.0));
	output.clip = output.position;
	output.worldXY = world.xy;
	output.lightPosRadInt = input.iPosRadInt;
	output.lightColHeight = input.iColHeight;
	return output;
}

[shader("fragment")]
float4 fragmentMain(VsOut input) : SV_Target
{
	// Reconstruct this fragment's G-buffer uv from its framebuffer position. SV_Position.xy is window-space pixel
	// coordinates with a top-left origin on BOTH backends, so it indexes the G-buffer texel directly — immune to
	// the projection Y-flip the backends bake in differently (the trap that once cost a long matrix hunt). The
	// framebuffer size isn't available as a push here (the instanced path's only push is the vertex MVP), so derive
	// 1/size from the clip-space gradient: screen.x = ndc.x·0.5+0.5 == px/W, so |ddx(screen.x)| == 1/W. This is the
	// same ddx-for-texel-size idiom ssao_blur.slang uses — dxc accepts it on DXIL where Sampler2D.GetDimensions does not.
	float2 ndc = input.clip.xy / input.clip.w;
	float2 screen = ndc * 0.5 + 0.5;
	float2 invSize = float2(abs(ddx(screen.x)), abs(ddy(screen.y)));
	float2 uv = input.position.xy * invSize;

	float4 albedo = albedoTex.Sample(uv);
	float3 normal = normalize(normalTex.Sample(uv).rgb * 2.0 - 1.0);

	float2 center = input.lightPosRadInt.xy;
	float radius = input.lightPosRadInt.z;
	float intensity = input.lightPosRadInt.w;
	float3 color = input.lightColHeight.rgb;
	float height = input.lightColHeight.w;

	// The light sits `height` above the 2D plane; the surface is at z=0. Attenuation uses the in-plane reach
	// (radius) — a 2D falloff; the direction for N·L is the full 3D vector, so flat (+Z) pixels still light and
	// the normal-mapped relief reads as 3D under a moving light.
	float2 toCenter = center - input.worldXY;
	float planar = length(toCenter);
	float atten = saturate(1.0 - planar / radius);
	atten *= atten; // smooth quadratic falloff to zero at the radius
	float3 L = normalize(float3(toCenter, height));
	float ndotl = max(dot(normal, L), 0.0);

	// Background pixels were cleared to albedo 0, so multiplying by albedo keeps empty space black with no branch
	// (and the undefined background normal can't leak through). HDR out — intensity > 1 feeds bloom.
	float3 lit = albedo.rgb * color * (intensity * ndotl * atten);
	return float4(lit, 1.0);
}
