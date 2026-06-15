// Forward directional lighting — the simplest "lit geometry" technique, the basis for most of the focused
// sandbox demos. One pass: it reads a Loom.Core.Vertex stream, transforms by the camera's MVP push constant
// (the same 64-byte vertex push mesh.slang uses), and shades each fragment with a single directional key light
// plus a flat ambient. The albedo is derived from the surface normal so each cube face reads as a distinct
// colour. Output is HDR (the key light pushes lit faces above 1.0) so the bloom and tone-map demos have
// highlights to work with; a demo that just wants the lit image blits it to an SDR backbuffer (which clamps).

struct PushConstants
{
	float4x4 mvp; // Loom.Core.Camera.ViewProjection (model is identity in the demos)
};

[[vk::push_constant]] PushConstants pc;

struct VsIn
{
	// HLSL semantics alongside the vk::location qualifiers: Slang pins the SPIR-V input locations from the
	// vk::location (Vulkan unchanged) AND emits the semantics for the HLSL/DXIL target (the DX12 backend), so
	// one source is cross-target. Without the semantics, dxc/fxc reject the entry ("semantic must be defined").
	[[vk::location(0)]] float3 position : POSITION;
	[[vk::location(1)]] float3 normal   : NORMAL;
	[[vk::location(2)]] float2 uv        : TEXCOORD0;
};

struct VsOut
{
	float4 position : SV_Position;
	float3 normal : NORMAL;
	float2 uv : TEXCOORD0; // passed through (the engine's vertex layout provides it) though the shading ignores it
};

[shader("vertex")]
VsOut vertexMain(VsIn input)
{
	VsOut output;
	output.position = mul(pc.mvp, float4(input.position, 1.0));
	output.normal = input.normal;
	output.uv = input.uv;
	return output;
}

[shader("fragment")]
float4 fragmentMain(VsOut input) : SV_Target
{
	float3 n = normalize(input.normal);
	float3 albedo = n * 0.5 + 0.5;                          // distinct per-face colour
	float3 lightDir = normalize(float3(0.45, 0.55, -0.6));   // matches the sandbox sun
	float ndotl = max(dot(n, lightDir), 0.0);

	const float ambient = 0.15;
	const float key = 1.5; // > 1 so the directly-lit faces are HDR — bloom/tone-map have something to catch
	return float4(albedo * (ambient + ndotl * key), 1.0);
}
