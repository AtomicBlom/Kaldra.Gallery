using SUIsei;

namespace Loom.Sandbox.UI;

/// <summary>
/// Code-behind for <c>UIInteractiveView.xaml</c>. The generator emits the partial half (typed
/// fields, <c>InitializeComponent</c>, event wires, <c>{x:Bind}</c> glue); this half supplies the VM,
/// handlers, and visual styling (HdrColor + Thickness — outside the generator's literal subset).
/// </summary>
internal sealed partial class UIInteractiveView
{
	public CounterViewModel ViewModel { get; } = new();

	public UIInteractiveView()
	{
		InitializeComponent();

		Root.Margin = new Thickness(24f, 24f, 0f, 0f);
		Root.Padding = new Thickness(20f, 16f, 20f, 16f);
		Root.Background = HdrColor.FromRgb(28, 32, 44, 220);
		Root.BorderBrush = HdrColor.FromRgb(120, 180, 255);
		Root.BorderThickness = new Thickness(2f);

		TitleLabel.Foreground = HdrColor.FromRgb(180, 210, 255);

		StatusLabel.Foreground = HdrColor.FromRgb(235, 240, 255);

		// Style both buttons identically (demonstrates the Style system applied via code-behind).
		var btnColor = HdrColor.FromRgb(50, 100, 200);
		IncrementButton.Background = btnColor;
		ResetButton.Background     = btnColor;
	}

	private void OnIncrementClick(object sender, RoutedEventArgs e) => ViewModel.Bump();

	private void OnResetClick(object sender, RoutedEventArgs e) => ViewModel.Reset();
}
