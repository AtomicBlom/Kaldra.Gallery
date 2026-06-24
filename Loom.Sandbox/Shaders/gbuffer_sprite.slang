// 2D sprite G-buffer (PlanningPhase2.md M14, Track A). Instanced unit quads (per-vertex binding 0) expanded by
// the per-instance SpriteInstance (binding 1, SandboxVertexLayouts.Sprite) into the deferred 2D G-buffer: albedo
// (SV_Target0), encoded world normal (SV_Target1), emissive (SV_Target2). Sprites are flat quads facing +Z with
// a fixed TBN (T=+X, B=+Y, N=+Z), so a tangent-space normal map gives them relief under the 2D light volumes
// (light2d.slang, M14-3). Albedo (material slot 0) + normal map (slot 1) come from the atlas; the per-instance
// uv-rect selects the cell — and the animation frame (M14-5). Alpha-tested for crisp pixel-art edges (no blend).
// Entry points vertexMain/fragmentMain so the Loom build emits DXIL.
//
// The per-instance stream arrives as four ISPRITE rows (ISPRITE0..3) — a single repeated semantic with an index,
// exactly like VertexLayout.Transform's ITRANSFORM, so DX12's semantic-name binding picks them up. The camera's
// view-projection arrives as a constants buffer the pass .Reads (the app composed the engine's ClipSpaceCorrection
// into it); it is read the same column-form way on both backends, with no matrix-layout special-casing.

[[vk::binding(0, 0)]] Sampler2D albedoTex; // atlas albedo (sRGB)
[[vk::binding(1, 0)]] Sampler2D normalTex; // atlas tangent-space normal map (linear)

struct ViewProj
{
	float4x4 viewProj; // the app's view-projection, clip-space-corrected
};

// The view-proj UBO follows the two samplers (binding 2); register(b1) — b0 is the unused root-constant slot.
[[vk::binding(2, 0)]] ConstantBuffer<ViewProj> view : register(b1);

struct VsIn
{
	// Per-vertex (binding 0 — VertexLayout.MeshStandard), locations 0..3. Normal/tangent are unused by sprites
	// but declared so the explicit locations line up with the layout (as instanced.slang does).
	[[vk::location(0)]] float3 corner : POSITION;       // unit-quad corner (0,0)..(1,1)
	[[vk::location(1)]] float3 normal : NORMAL;         // unused (sprites use a fixed TBN)
	[[vk::location(2)]] float2 quadUv : TEXCOORD0;      // == corner.xy (the quad's own uv)
	[[vk::location(3)]] float4 tangent : TANGENT;       // unused
	// Per-instance SpriteInstance (binding 1 — SandboxVertexLayouts.Sprite), locations continue at 4..7:
	[[vk::location(4)]] float4 iXformRot : ISPRITE0;    // position.xy, depth, rotation
	[[vk::location(5)]] float4 iSizePivot : ISPRITE1;   // size.xy, pivot.xy
	[[vk::location(6)]] float4 iUvRect : ISPRITE2;      // atlas rect: u0, v0, u1, v1
	[[vk::location(7)]] float4 iTint : ISPRITE3;        // rgba
};

struct VsOut
{
	float4 position : SV_Position;
	float2 uv : TEXCOORD0;
	float4 tint : TEXCOORD1;
};

struct GBuffer
{
	float4 albedo : SV_Target0;
	float4 normal : SV_Target1;
	float4 emissive : SV_Target2;
};

[shader("vertex")]
VsOut vertexMain(VsIn input)
{
	float2 position = input.iXformRot.xy;
	float depth = input.iXformRot.z;
	float rot = input.iXformRot.w;
	float2 size = input.iSizePivot.xy;
	float2 pivot = input.iSizePivot.zw;

	// Expand the unit corner about the pivot, scale to size, rotate, translate to the world position.
	float2 local = (input.corner.xy - pivot) * size;
	float s = sin(rot), c = cos(rot);
	float2 rotated = float2(local.x * c - local.y * s, local.x * s + local.y * c);
	float3 world = float3(position + rotated, depth);

	VsOut output;
	output.position = mul(view.viewProj, float4(world, 1.0));
	// Map the quad corner into the atlas cell; flip v so the cell's top row lands at the quad's top (+Y up world).
	float2 t = float2(input.quadUv.x, 1.0 - input.quadUv.y);
	output.uv = lerp(input.iUvRect.xy, input.iUvRect.zw, t);
	output.tint = input.iTint;
	return output;
}

[shader("fragment")]
GBuffer fragmentMain(VsOut input)
{
	float4 texel = albedoTex.Sample(input.uv);
	if (texel.a < 0.5)
		discard; // alpha-test: crisp pixel-art edges, no blend

	// Fixed sprite TBN (T=+X, B=+Y, N=+Z) → the world normal is just the decoded tangent-space sample.
	float3 ts = normalTex.Sample(input.uv).rgb * 2.0 - 1.0;
	float3 worldN = normalize(float3(ts.x, ts.y, ts.z));

	GBuffer output;
	output.albedo = float4(texel.rgb * input.tint.rgb, 1.0);
	output.normal = float4(worldN * 0.5 + 0.5, 1.0);   // encode [-1,1] → [0,1] for the lighting pass
	output.emissive = float4(0.0, 0.0, 0.0, 1.0);      // 2D emissive channel (glowing sprites later); unlit for now
	return output;
}
