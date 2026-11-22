using System.Numerics;
using Loom.Core.Geometry;
using Loom.Core.Graph;
using Loom.Core.Hosting;
using Loom.Gallery.Demos;

namespace Loom.Tests;

/// <summary>
/// The two guards that make an element-declared <see cref="BufferHandle"/> worth more than a byte count: a draw may
/// not ask for more instances than the buffer holds, and a technique may not walk it at a stride its elements don't
/// have. Both failures are silent on the GPU — the input assembler reads whatever follows the buffer — so they are
/// checked on the CPU, and identically on every backend.
/// </summary>
public class InstanceStreamContractTests
{
	[Test]
	public void Gpu_instance_source_accepts_a_count_the_buffer_holds()
	{
		BufferHandle? buffer = null;
		RenderGraph.Define(g => buffer = g.Buffer("particles", 512, VertexLayout.Transform));

		DrawCall.Gpu(buffer!, 512).Count.ShouldBe(512);
		DrawCall.Gpu(buffer!, 100).Count.ShouldBe(100); // drawing fewer than declared is a partial spawn, not an error
	}

	[Test]
	public void Gpu_instance_source_rejects_more_instances_than_the_buffer_holds()
	{
		BufferHandle? buffer = null;
		RenderGraph.Define(g => buffer = g.Buffer("particles", 512, VertexLayout.Transform));

		var ex = Should.Throw<ArgumentOutOfRangeException>(() => DrawCall.Gpu(buffer!, 4096));
		ex.Message.ShouldContain("particles");
		ex.Message.ShouldContain("512");
	}

	[Test]
	public void Gpu_instance_source_rejects_a_negative_count()
	{
		BufferHandle? buffer = null;
		RenderGraph.Define(g => buffer = g.Buffer<Matrix4x4>("instances", 8));

		Should.Throw<ArgumentOutOfRangeException>(() => DrawCall.Gpu(buffer!, -1));
	}

	[Test]
	public void Gpu_instance_source_cannot_check_a_byte_sized_buffer()
	{
		// The byte overload declares no element count, so there is nothing to check the draw against. This asserts the
		// guard stays silent rather than inventing a contract — the reason the typed overloads are worth migrating to.
		BufferHandle? buffer = null;
		RenderGraph.Define(g => buffer = g.Buffer("opaque", 512 * 64));

		DrawCall.Gpu(buffer!, 999_999).Count.ShouldBe(999_999);
	}

	[Test]
	public void Matching_instance_stride_passes()
	{
		BufferHandle? buffer = null;
		RenderGraph.Define(g => buffer = g.Buffer("instances", 32, VertexLayout.Transform));

		buffer!.CheckInstanceStride(VertexLayout.Transform.Stride, "foliage");
	}

	[Test]
	public void Mismatched_instance_stride_is_rejected()
	{
		// The realistic mistake: a buffer declared with the position-only particle layout drawn by a technique whose
		// instance stream is the fatter position+velocity one. The draw would read two elements per instance.
		BufferHandle? buffer = null;
		RenderGraph.Define(g => buffer = g.Buffer("particles", 512, VertexLayout.Transform));

		var ex = Should.Throw<InvalidOperationException>(() => buffer!.CheckInstanceStride(16, "particle.render"));
		ex.Message.ShouldContain("particle.render");
		ex.Message.ShouldContain("particles");
		ex.Message.ShouldContain("16");
		ex.Message.ShouldContain("64");
	}

	[Test]
	[Arguments(0, 64)] // the technique declares no per-instance stream
	[Arguments(16, 0)] // the buffer was declared as opaque bytes
	public void Instance_stride_check_needs_both_sides_to_be_known(int techniqueStride, int bufferStride)
	{
		BufferHandle? buffer = null;
		RenderGraph.Define(g => buffer = bufferStride == 0
			? g.Buffer("stream", 512 * 16)
			: g.Buffer("stream", 512, VertexLayout.Transform));

		buffer!.CheckInstanceStride(techniqueStride, "whatever");
	}

	[Test]
	public void The_demo_particle_layouts_still_have_the_strides_their_buffers_are_sized_from()
	{
		// The gallery's GPU-particle buffers are declared as count x one of these layouts, and their compute shaders
		// write a matching struct. If a layout gained an attribute, the buffer would silently resize under the shader.
		SandboxVertexLayouts.ParticlePosition.Stride.ShouldBe(16); // one float4: position.xyz + life
		SandboxVertexLayouts.Particle.Stride.ShouldBe(32);         // two float4s: position+life, velocity+seed
	}
}
