using SUIsei;
using SUIsei.Controls;
using SUIsei.RoutedEvents;

namespace Loom.Web.UI;

/// <summary>
/// Code-behind for <c>InWorldPanelView.xaml</c> (hand-ported from the sandbox verbatim so the browser capture
/// matches the desktop reference pixel for pixel) — the content of the in-world UI panel demo. An interactive
/// counter proves that pointer events are correctly projected from screen space onto the panel.
/// </summary>
internal sealed partial class InWorldPanelView
{
	public CounterViewModel ViewModel { get; } = new();

	public InWorldPanelView()
	{
		InitializeComponent();

		PanelBody.Padding = new Thickness(20f, 16f, 20f, 16f);
		PanelBody.Background = HdrColor.FromRgb(18, 24, 38, 230);
		PanelBody.BorderBrush = HdrColor.FromRgb(80, 200, 140);
		PanelBody.BorderThickness = new Thickness(2f);

		StatusLabel.Foreground = HdrColor.FromRgb(210, 230, 220);

		var btnColor = HdrColor.FromRgb(30, 120, 80);
		IncrementButton.Background = btnColor;
		ResetButton.Background     = btnColor;
	}

	private void OnIncrementClick(object sender, RoutedEventArgs e) => ViewModel.Bump();

	private void OnResetClick(object sender, RoutedEventArgs e) => ViewModel.Reset();
}
