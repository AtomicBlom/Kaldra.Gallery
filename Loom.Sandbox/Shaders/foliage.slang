// Instanced foliage with vertex-shader wind + alpha-test cutout (M15-5). Each instance is a blade/card placed +
// yaw-rotated + scaled from its per-instance data; the vertex shader sways it by the per-frame wind (read from
// the M15-0 uniform seam — vertex-visible), weighted by the vertex's height so the base stays planted and the
// tip moves most, and offset by the instance's phase so the field ripples rather than moving in lockstep. The
// fragment alpha-tests the blade silhouette (a plain discard on an opaque technique — no special pipeline
// state) and writes the deferred g-buffer, so the same deferred lighting shades it.

[[vk::binding(0, 0)]] Sampler2D bladeTex; // blade colour + alpha cutout (alpha < cutoff → discard)

// The camera view-projection (clip-space-corrected) the pass .Reads — the first UBO after the sampler (binding 1,
// register b1). The per-instance world transform is rebuilt from the instance stream below, so there is no
// per-draw model block; the wind parameters ride the per-draw Uniforms slot that follows (binding 2, register b2).
struct ViewProj { float4x4 viewProj; };
[[vk::binding(1, 0)]] ConstantBuffer<ViewProj> view : register(b1);

struct WindParams
{
	float4 dirTime;  // wind direction.xy, time (.z), _
	float4 strength; // amplitude (.x), frequency (.y), _, _
};
[[vk::binding(2, 0)]] ConstantBuffer<WindParams> wind : register(b2);

struct VsIn
{
	[[vk::location(0)]] float3 position : POSITION; // blade-local: x ∈ [-0.5,0.5], y ∈ [0,1] (base→tip)
	[[vk::location(1)]] float3 normal : NORMAL;
	[[vk::location(2)]] float2 uv : TEXCOORD;
	[[vk::location(3)]] float4 tangent : TANGENT;
	[[vk::location(4)]] float4 inst0 : IFOLIAGE0; // world position.xyz, scale
	[[vk::location(5)]] float4 inst1 : IFOLIAGE1; // yaw, phase, stiffness, _
};

struct VsOut
{
	float4 position : SV_Position;
	float2 uv : TEXCOORD0;
	float3 worldPos : POSITION0;
	float3 normal : NORMAL0;
};

[shader("vertex")]
VsOut vertexMain(VsIn input)
{
	float scale = input.inst0.w;
	float yaw = input.inst1.x;
	float phase = input.inst1.y;
	float stiffness = max(input.inst1.z, 0.1);

	// Yaw-rotate the blade about Y, then scale.
	float c = cos(yaw), s = sin(yaw);
	float3 local = float3(input.position.x * c - input.position.z * s, input.position.y, input.position.x * s + input.position.z * c) * scale;

	// Height-weighted wind sway: the base (local.y small) stays put, the tip swings; per-instance phase decorrelates.
	float height = input.position.y; // 0 at base, 1 at tip
	float sway = sin(wind.dirTime.z * wind.strength.y + phase) * wind.strength.x * pow(height, stiffness);
	float3 world = input.inst0.xyz + local;
	world.x += wind.dirTime.x * sway;
	world.z += wind.dirTime.y * sway;

	VsOut output;
	output.position = mul(view.viewProj, float4(world, 1.0));
	output.uv = input.uv;
	output.worldPos = world;
	output.normal = float3(0.0, 1.0, 0.0); // blades read as upward-facing for soft, even lighting
	return output;
}

struct GBuffer
{
	float4 albedo : SV_Target0;
	float4 normal : SV_Target1;
	float4 position : SV_Target2;
};

[shader("fragment")]
GBuffer fragmentMain(VsOut input)
{
	float4 blade = bladeTex.Sample(input.uv);
	if (blade.a < 0.5)
		discard; // alpha-test cutout — the blade silhouette

	GBuffer output;
	output.albedo = float4(blade.rgb, 1.0);
	output.normal = float4(input.normal * 0.5 + 0.5, 1.0); // encode [-1,1] → [0,1]
	output.position = float4(input.worldPos, 1.0);
	return output;
}
