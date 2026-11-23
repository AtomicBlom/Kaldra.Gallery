using Vysara;
using Vysara.Media;
using Vysara.RoutedEvents;

namespace Loom.Gallery.UI;

/// <summary>
/// Code-behind for <c>UIInteractiveView.xaml</c>. The generator emits the partial half (typed
/// fields, <c>InitializeComponent</c>, event wires, <c>{x:Bind}</c> glue); this half supplies the VM,
/// handlers, and the visual styling, set as brushes from code.
/// </summary>
internal sealed partial class UIInteractiveView
{
	public CounterViewModel ViewModel { get; } = new();

	public UIInteractiveView()
	{
		InitializeComponent();
		Background = new SolidColorBrush(HdrColor.FromRgb(28, 32, 44, 220));

		TitleLabel.Foreground = new SolidColorBrush(HdrColor.FromRgb(180, 210, 255));

		StatusLabel.Foreground = new SolidColorBrush(HdrColor.FromRgb(235, 240, 255));

		// Style both buttons identically (demonstrates the Style system applied via code-behind). One brush serves
		// both: the button template shows it in its Normal state and never animates it.
		var btnBrush = new SolidColorBrush(HdrColor.FromRgb(50, 100, 200));
		IncrementButton.Background = btnBrush;
		ResetButton.Background = btnBrush;
	}

	private void OnIncrementClick(object sender, RoutedEventArgs e) => ViewModel.Bump();

	private void OnResetClick(object sender, RoutedEventArgs e) => ViewModel.Reset();
}
