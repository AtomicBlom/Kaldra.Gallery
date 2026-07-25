using System.Numerics;

namespace Loom.Gallery;

/// <summary>
/// Time-of-day lighting policy: maps a day phase (0..1, 0 = sunrise, 0.25 = noon, 0.5 = sunset,
/// 0.75 = midnight) to the sun direction + the colours the deferred lighting and sky passes consume — a warm
/// low sun and orange horizon near dawn/dusk, a white sun and blue sky at noon, a dim blue night. Host-side
/// art direction; the engine just receives the resulting matrices/colours each frame (pulled, not captured).
/// </summary>
internal static class DayNight
{
	internal readonly record struct State(
		Vector3 SunDir, Vector3 SunColor, float Intensity, Vector3 Ambient, Vector3 Zenith, Vector3 Horizon);

	public static State Compute(float phase)
	{
		// The sun arcs across the sky: up at noon, below the horizon at night, travelling along X with a fixed
		// Z tilt so shadows fall at a pleasing angle.
		var a = phase * MathF.Tau;
		var height = MathF.Sin(a);                                // -1..1; > 0 = above the horizon (day)
		var sunDir = Vector3.Normalize(new Vector3(MathF.Cos(a), MathF.Max(height, 0.05f), -0.45f));

		var day = Saturate(height * 3f);          // 0 at/below horizon, 1 well into the day
		var golden = Saturate(1f - MathF.Abs(height) * 5f); // peaks as the sun grazes the horizon

		// Sun light: warm near the horizon, white high; extinguished at night.
		var warm = new Vector3(1.0f, 0.55f, 0.25f);
		var white = new Vector3(1.0f, 0.97f, 0.9f);
		var sunColor = Vector3.Lerp(warm, white, Saturate(height * 2f));
		var intensity = day * 2.0f;

		// Ambient: a dim blue night → a soft daylight, warmed at golden hour.
		var nightAmbient = new Vector3(0.03f, 0.04f, 0.08f);
		var dayAmbient = new Vector3(0.28f, 0.30f, 0.34f);
		var ambient = Vector3.Lerp(nightAmbient, dayAmbient, day) + warm * (golden * 0.10f);

		// Sky gradient: night blues → day blues, with a warm horizon at golden hour.
		var zenith = Vector3.Lerp(new Vector3(0.02f, 0.02f, 0.06f), new Vector3(0.20f, 0.42f, 0.85f), day);
		var dayHorizon = new Vector3(0.55f, 0.72f, 0.95f);
		var horizon = Vector3.Lerp(new Vector3(0.03f, 0.03f, 0.08f), dayHorizon, day);
		horizon = Vector3.Lerp(horizon, new Vector3(0.95f, 0.55f, 0.30f), golden); // golden-hour warm band

		return new State(sunDir, sunColor, intensity, ambient, zenith, horizon);
	}

	private static float Saturate(float v) => MathF.Max(0f, MathF.Min(1f, v));
}
