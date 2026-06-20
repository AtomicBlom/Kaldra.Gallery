using Loom.UI;

namespace Loom.Sandbox.UI;

/// <summary>
/// Code-behind for <c>UIInteractiveView.xaml</c> (MU2). The generator emits the partial half — typed
/// <c>ActionButton</c>/<c>StatusLabel</c> fields, the tree-building <c>InitializeComponent()</c>, the
/// <c>ActionButton.Click += OnButtonClick</c> wiring, and the <c>{x:Bind ViewModel.Status}</c> binding glue. This
/// half supplies the bind root (<see cref="ViewModel"/>), the click handler, and the colours/thickness the MU2 XAML
/// subset can't express as literals yet (HdrColor / Thickness parsing arrives in a later milestone).
/// </summary>
internal sealed partial class UIInteractiveView
{
	public CounterViewModel ViewModel { get; } = new();

	public UIInteractiveView()
	{
		InitializeComponent();

		// Visual styling the MU2 literal subset doesn't cover (HdrColor + Thickness): set in code-behind.
		Root.Margin = new Thickness(24f, 24f, 0f, 0f);
		Root.Padding = new Thickness(20f, 16f, 20f, 16f);
		Root.Background = HdrColor.FromRgb(28, 32, 44, 220);
		Root.BorderBrush = HdrColor.FromRgb(120, 180, 255);
		Root.BorderThickness = new Thickness(2f);
		StatusLabel.Foreground = HdrColor.FromRgb(235, 240, 255);
	}

	private void OnButtonClick(object sender, RoutedEventArgs e) => ViewModel.Bump();
}
