using SUIsei;
using SUIsei.Controls;

namespace Loom.Sandbox.Demos;

/// <summary>A tiny shared builder for a debug panel's label + slider pair, so demos that expose numeric tunables
/// (bloom threshold, wind strength, boss scale, …) don't each hand-roll the same wiring: the label's text tracks
/// the live value, and <paramref name="onChanged"/> is the demo's own setter (a private field assignment) — the
/// slider is the only source of truth, so no external re-sync is needed.</summary>
internal static class DebugSlider
{
	public static UIElement Labeled(string label, float value, float min, float max, Action<float> onChanged)
	{
		var text = new TextBlock { Text = $"{label}: {value:F2}", Margin = new Thickness(0f, 6f, 0f, 2f) };
		var slider = new Slider { Minimum = min, Maximum = max, Value = value };
		slider.ValueChanged += (_, _) =>
		{
			var v = (float)slider.Value;
			text.Text = $"{label}: {v:F2}";
			onChanged(v);
		};
		var stack = new StackPanel();
		stack.Children.Add(text);
		stack.Children.Add(slider);
		return stack;
	}
}
