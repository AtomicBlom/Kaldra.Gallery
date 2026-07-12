namespace Loom.Web.Demos;

/// <summary>
/// Loads demo assets over HTTP from static files under <c>wwwroot/assets/</c> — the browser has no filesystem, so
/// this is the counterpart to the desktop host's <c>File.ReadAllBytesAsync</c>, fetching by the same
/// desktop-style relative path (e.g. <c>kenney/castle/tree-large.glb</c>) both hosts share.
/// </summary>
internal static class WebAssets
{
	/// <summary>Fetch the bytes of the asset <paramref name="name"/> (e.g. <c>rigged_bar.glb</c>) from
	/// <c>assets/</c>, relative to the served root.</summary>
	public static async Task<byte[]> LoadAsync(string name)
	{
		var base64 = await Host.FetchAssetBase64($"assets/{name}");
		return Convert.FromBase64String(base64);
	}
}
