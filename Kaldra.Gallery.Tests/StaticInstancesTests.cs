using BinaryVibrance.Kaldra.Backend.DirectX12;
using BinaryVibrance.Kaldra.Backend.Vulkan;
using BinaryVibrance.Kaldra.Core.Hosting;
using Kaldra.Gallery.Demos;

namespace BinaryVibrance.Kaldra.Tests;

/// <summary>
/// Instances uploaded once (<see cref="IEngine.CreateInstances{T}"/>, drawn by range with
/// <see cref="DrawCall.Static(Core.Geometry.InstanceBuffer, int, int)"/>) draw what the same instances uploaded every
/// frame do: the instancing demo's field, drawn both ways on each backend present, comes out the same frame. GPU-gated:
/// skipped where neither backend is usable. WebGPU's leg is the browser gallery's <c>instancing-once</c>.
/// </summary>
[NotInParallel] // one GPU engine at a time
public class StaticInstancesTests
{
	[Test]
	public void Instances_uploaded_once_draw_what_instances_uploaded_each_frame_do()
	{
		_ = new VulkanEngineSettings();
		_ = new DirectX12EngineSettings();
		var backends = Backends.Available.Where(b => b.Name is "Vulkan" or "DirectX12").ToList();
		if (backends.Count == 0)
			throw new TUnit.Core.Exceptions.SkipTestException("Neither Vulkan nor DirectX12 is available on this box.");

		var failures = new List<string>();
		foreach (var backend in backends)
		{
			var eachFrame = Path.Combine(Path.GetTempPath(), $"kaldra-instances-frame-{backend.Name}-{Guid.NewGuid():N}.png");
			var once = Path.Combine(Path.GetTempPath(), $"kaldra-instances-once-{backend.Name}-{Guid.NewGuid():N}.png");
			try
			{
				GalleryCapture.TryRender(backend, () => new InstancingDemo(), eachFrame).ShouldBeTrue();
				GalleryCapture.TryRender(backend, () => new InstancingDemo(uploadOnce: true), once).ShouldBeTrue();
				var (w, h, a) = PngReader.Read(eachFrame);
				var (_, _, b) = PngReader.Read(once);

				// Not two empty frames: the field covers a good part of it
				int drawn = 0;
				for (int i = 0; i < a.Length; i += 4)
					if (a[i] != a[0] || a[i + 1] != a[1] || a[i + 2] != a[2]) drawn++;
				if (drawn < w * h / 20)
					failures.Add($"{backend.Name}: only {drawn} pixels drawn, so the frames prove nothing");

				var diff = ImageComparison.Compare(w, h, a, b, perChannelTolerance: 1);
				if (diff.FailedFraction > 0)
					failures.Add($"{backend.Name}: {diff}");
			}
			finally
			{
				if (File.Exists(eachFrame)) File.Delete(eachFrame);
				if (File.Exists(once)) File.Delete(once);
			}
		}
		failures.ShouldBeEmpty("Instances uploaded once drew differently:\n  " + string.Join("\n  ", failures));
	}

	/// <summary>
	/// Drawn in ranges of 333 instances, so all but the first draw of a run start part way into the buffer (and off a row's
	/// boundary), the field still comes out as uploading it every frame does. This is where the backends differ: DirectX 12
	/// offsets the instance stream's view, Vulkan and WebGPU bind the buffer at an offset. A wrong offset draws an earlier
	/// range's cubes again and leaves later ones out.
	/// </summary>
	[Test]
	public void Ranges_starting_part_way_into_the_buffer_draw_their_own_instances()
	{
		_ = new VulkanEngineSettings();
		_ = new DirectX12EngineSettings();
		var backends = Backends.Available.Where(b => b.Name is "Vulkan" or "DirectX12").ToList();
		if (backends.Count == 0)
			throw new TUnit.Core.Exceptions.SkipTestException("Neither Vulkan nor DirectX12 is available on this box.");

		var failures = new List<string>();
		foreach (var backend in backends)
		{
			var eachFrame = Path.Combine(Path.GetTempPath(), $"kaldra-instances-frame-{backend.Name}-{Guid.NewGuid():N}.png");
			var ranged = Path.Combine(Path.GetTempPath(), $"kaldra-instances-ranged-{backend.Name}-{Guid.NewGuid():N}.png");
			try
			{
				InstancingDemo? demo = null;
				GalleryCapture.TryRender(backend, () => new InstancingDemo(), eachFrame).ShouldBeTrue();
				GalleryCapture.TryRender(backend, () => demo = new InstancingDemo(uploadOnce: true, rangeSize: 333), ranged).ShouldBeTrue();
				demo!.OffsetRanges.ShouldBeGreaterThan(0, $"{backend.Name}: no draw started past the buffer's first instance, so this proves nothing");

				var (w, h, a) = PngReader.Read(eachFrame);
				var (_, _, b) = PngReader.Read(ranged);
				var diff = ImageComparison.Compare(w, h, a, b, perChannelTolerance: 1);
				if (diff.FailedFraction > 0)
					failures.Add($"{backend.Name}: {diff}");
			}
			finally
			{
				if (File.Exists(eachFrame)) File.Delete(eachFrame);
				if (File.Exists(ranged)) File.Delete(ranged);
			}
		}
		failures.ShouldBeEmpty("Instances drawn in ranges drew differently:\n  " + string.Join("\n  ", failures));
	}
}
