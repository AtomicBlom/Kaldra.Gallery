namespace BinaryVibrance.Kaldra.Tests;

/// <summary>Result of comparing two RGBA images (golden harness): the worst and average per-channel difference,
/// and how many pixels exceed a per-channel tolerance.</summary>
internal readonly record struct ImageDiff(int Width, int Height, int MaxChannelDiff, double MeanChannelDiff, int FailedPixels, int TotalPixels)
{
	public double FailedFraction => TotalPixels == 0 ? 0 : (double)FailedPixels / TotalPixels;
	public override string ToString() =>
		$"{Width}x{Height}: maxDiff={MaxChannelDiff}, meanDiff={MeanChannelDiff:F3}, failed={FailedPixels}/{TotalPixels} ({FailedFraction:P2})";
}
