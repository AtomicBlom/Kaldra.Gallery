using System.Reflection;

namespace Loom.Web.Demos;

/// <summary>
/// Loads demo assets embedded into the WASM assembly. The browser has no filesystem, so binary assets (glTF models,
/// textures) that the sandbox reads from <c>AppContext.BaseDirectory</c> are instead compiled in as embedded
/// resources under the <c>Loom.Web.Assets.</c> logical-name prefix and resolved here by file name.
/// </summary>
internal static class WebAssets
{
	/// <summary>The bytes of the embedded asset <paramref name="name"/> (e.g. <c>rigged_bar.glb</c> or the
	/// desktop-style relative path <c>kenney/castle/tree-large.glb</c> — embedded resource logical names can't
	/// contain slashes, so they're embedded dot-separated and looked up the same way here).</summary>
	public static byte[] Load(string name)
	{
		var assembly = Assembly.GetExecutingAssembly();
		var logicalName = $"Loom.Web.Assets.{name.Replace('/', '.')}";
		using var stream = assembly.GetManifestResourceStream(logicalName)
			?? throw new FileNotFoundException($"Embedded asset '{logicalName}' not found.");
		var bytes = new byte[stream.Length];
		stream.ReadExactly(bytes);
		return bytes;
	}
}
