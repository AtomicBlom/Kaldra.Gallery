using System.Numerics;

using SUIsei;
using SUIsei.Controls;
using SUIsei.RoutedEvents;

namespace Loom.Gallery.UI;

/// <summary>
/// Code-behind for <c>InWorldPanelView.xaml</c> — the content of the in-world UI panel demo.
/// An interactive counter proves that pointer events are correctly projected from screen space onto the panel.
/// </summary>
internal sealed partial class InWorldPanelView
{
	public CounterViewModel ViewModel { get; } = new();

	public InWorldPanelView()
	{
		InitializeComponent();

		//Root.Background = HdrColor.FromRgb(10, 15, 25, 210);

		PanelBody.Padding = new Thickness(20f, 16f, 20f, 16f);
		PanelBody.Background = HdrColor.FromRgb(18, 24, 38, 230);
		PanelBody.BorderBrush = HdrColor.FromRgb(80, 200, 140);
		PanelBody.BorderThickness = new Thickness(2f);

		//TitleLabel.Foreground = HdrColor.FromRgb(80, 200, 140);
		StatusLabel.Foreground = HdrColor.FromRgb(210, 230, 220);

		var btnColor = HdrColor.FromRgb(30, 120, 80);
		IncrementButton.Background = btnColor;
		ResetButton.Background = btnColor;
	}

	private void OnIncrementClick(object sender, RoutedEventArgs e) => ViewModel.Bump();

	private void OnResetClick(object sender, RoutedEventArgs e) => ViewModel.Reset();
}
