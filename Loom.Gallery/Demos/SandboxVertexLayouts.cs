using Loom.Core.Geometry;

namespace Loom.Gallery.Demos;

/// <summary>
/// The per-instance vertex formats the demos use for the game-track effects — sprites, 2D lights, GPU particles,
/// and foliage. These are <em>content</em> contracts between a specific sandbox shader and the data a demo feeds
/// it, so they live with the demos rather than in <see cref="Loom.Core.Geometry.VertexLayout"/> (which keeps only
/// the engine-neutral layouts: the mesh/skinned-mesh streams, the generic instanced <see cref="VertexLayout.Transform"/>,
/// and the UI <see cref="VertexLayout.Overlay"/>). Each is the shared contract between a demo's instance data and
/// the technique that draws it.
/// </summary>
internal static class SandboxVertexLayouts
{
	/// <summary>
	/// The per-instance stream for 2D sprite/tile batching — one <see cref="SpriteInstance"/> per quad
	/// (4 × float4, 64 bytes) at binding 1, advancing one per instance. The four rows carry the sprite's
	/// world transform (position + depth + rotation), its world size + rotation pivot, its atlas uv-rect (the
	/// sub-image, which doubles as the sprite-sheet animation frame), and an rgba tint. Paired with a unit-quad
	/// per-vertex stream (<see cref="VertexLayout.MeshStandard"/>): the vertex shader expands each corner into the
	/// quad the instance describes and looks the atlas region up from the uv-rect.
	/// <para>
	/// The four rows share one repeated semantic (<c>ISPRITE0..3</c>), exactly like <see cref="VertexLayout.Transform"/>'s
	/// <c>ITRANSFORM</c> — DirectX12 binds per-instance attributes by semantic name + index, and four <em>distinct</em>
	/// per-instance semantics bind as zero there (the quads collapse), so an indexed-row layout is required.
	/// </para>
	/// </summary>
	public static VertexLayout Sprite { get; } = new(
		VertexInputRate.PerInstance,
		binding: 1,
		new VertexAttribute("ISPRITE", VertexFormat.Float4), // position.xy, depth, rotation
		new VertexAttribute("ISPRITE", VertexFormat.Float4), // size.xy, pivot.xy
		new VertexAttribute("ISPRITE", VertexFormat.Float4), // atlas uv-rect: u0, v0, u1, v1
		new VertexAttribute("ISPRITE", VertexFormat.Float4)); // rgba tint

	/// <summary>
	/// The per-instance stream for 2D point lights — one <see cref="Light2DInstance"/> per light
	/// (2 × float4, 32 bytes) at binding 1, advancing one per instance. Paired with the same unit-quad
	/// per-vertex stream (<see cref="VertexLayout.MeshStandard"/>) the sprites use: the light shader expands the quad
	/// to the light's bounding box, then samples the 2D G-buffer normal per covered pixel and accumulates the light
	/// additively. The two rows carry the light's world position + radius + intensity, and its colour + height
	/// off the plane. As with <see cref="Sprite"/>/<see cref="VertexLayout.Transform"/>, the rows share one repeated
	/// semantic (<c>ILIGHT0..1</c>) so DirectX12's semantic-name-plus-index binding picks them up.
	/// </summary>
	public static VertexLayout Light2D { get; } = new(
		VertexInputRate.PerInstance,
		binding: 1,
		new VertexAttribute("ILIGHT", VertexFormat.Float4),  // position.xy, radius, intensity
		new VertexAttribute("ILIGHT", VertexFormat.Float4)); // colour.rgb, height

	/// <summary>
	/// The per-instance stream for the textured, colour-over-life GPU particle system — one particle per instance
	/// (2 × float4, 32 bytes) at <em>binding 0</em>, advancing one per instance. Unlike <see cref="Sprite"/>/
	/// <see cref="Light2D"/>, particles have <em>no</em> paired per-vertex mesh: a compute pass writes this stream
	/// into a storage buffer (<c>RWStructuredBuffer</c> of the matching 32-byte struct) and the render shader
	/// fabricates a billboard quad per instance from <c>SV_VertexID</c> — so the stream owns binding 0 outright.
	/// The two rows carry position + remaining-life and velocity + per-particle seed; the repeated
	/// <c>IPARTICLE0..1</c> semantic matches DirectX12's name-plus-index binding. See <see cref="ParticlePosition"/>
	/// for the lighter position-only variant.
	/// </summary>
	public static VertexLayout Particle { get; } = new(
		VertexInputRate.PerInstance,
		binding: 0,
		new VertexAttribute("IPARTICLE", VertexFormat.Float4),  // position.xyz, life (1 at spawn → 0 at death)
		new VertexAttribute("IPARTICLE", VertexFormat.Float4)); // velocity.xyz, seed (per-particle random)

	/// <summary>
	/// The minimal per-instance particle stream — a single <c>float4</c> (xyz position + w remaining-life),
	/// 16 bytes, owning <em>binding 0</em> and advancing one per instance. Like <see cref="Particle"/> it has no
	/// paired per-vertex mesh: a compute pass writes one <c>float4</c> per particle into a storage buffer and the
	/// render shader fabricates a billboard quad from <c>SV_VertexID</c>. It carries only position+life (no velocity
	/// or seed), so a particle shader that needs nothing more binds this lighter stream. The single attribute uses
	/// the <c>POSITION</c> semantic, matching the shader's location-0 input.
	/// </summary>
	public static VertexLayout ParticlePosition { get; } = new(
		VertexInputRate.PerInstance,
		binding: 0,
		new VertexAttribute("POSITION", VertexFormat.Float4)); // position.xyz, life (1 at spawn → 0 at death)

	/// <summary>
	/// The per-instance stream for foliage instancing — one plant per instance (2 × float4, 32 bytes) at
	/// binding 1, paired with a blade/card per-vertex mesh (<see cref="VertexLayout.MeshStandard"/>). The two rows
	/// carry the plant's world position + uniform scale, and its yaw + a wind phase + bend stiffness. The instanced
	/// foliage vertex shader rebuilds each plant's transform, sways it by the per-frame wind (height-weighted, offset
	/// by the per-instance phase so the field doesn't move in lockstep), and alpha-tests the blade cutout. As with the
	/// other per-instance layouts, the rows share one repeated semantic (<c>IFOLIAGE0..1</c>) for DirectX12's binding.
	/// </summary>
	public static VertexLayout Foliage { get; } = new(
		VertexInputRate.PerInstance,
		binding: 1,
		new VertexAttribute("IFOLIAGE", VertexFormat.Float4),  // world position.xyz, uniform scale
		new VertexAttribute("IFOLIAGE", VertexFormat.Float4)); // yaw, wind phase, bend stiffness, pad
}
