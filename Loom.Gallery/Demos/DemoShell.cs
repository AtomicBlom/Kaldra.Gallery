using Vysara;
using Vysara.Controls;
using Vysara.Media;

namespace Loom.Gallery.Demos;

/// <summary>
/// The chrome every demo renders under: a demo switcher, a debug panel, and a status bar, all Vysara — so it
/// renders identically on desktop and in the browser (unlike the old per-host Dear ImGui menu bar, which the
/// browser build can't reference at all). A full-area 3-row <see cref="Grid"/>:
/// <list type="bullet">
/// <item>top — a <see cref="MenuBar"/> ("Demos" switcher) + the active demo's name + the engine name + FPS;</item>
/// <item>middle — a top-left debug panel (host toggles + the active demo's own <see cref="IDemo.DebugPanel"/>,
/// collapsible via a pin button) and, centred, the active demo's own UI content when it hosts one (the UI-track
/// demos, whose Vysara tree <em>is</em> the subject of the demo — always visible, HUD or not);</item>
/// <item>bottom — a status line + an optional right-aligned region a demo writes into (<see cref="IDemo.HudStatus"/>).</item>
/// </list>
/// <see cref="SetHudVisible"/> hides the top bar and the debug panel (the <c>--no-hud</c> chrome-free capture) but
/// never the centred demo content — that content is the demo's subject, not chrome.
/// </summary>
internal sealed class DemoShell
{
	// Per shell rather than static: a brush tracks the live elements showing it without locking, and shells in separate
	// hosts can run on separate threads.
	private readonly SolidColorBrush _barBackground = new(HdrColor.FromRgb(24, 26, 32));
	private readonly SolidColorBrush _panelBackground = new(HdrColor.FromRgb(28, 32, 44, 220));
	private readonly SolidColorBrush _accentText = new(HdrColor.FromRgb(120, 180, 255));
	private readonly SolidColorBrush _primaryText = new(HdrColor.FromRgb(235, 240, 255));
	private readonly SolidColorBrush _secondaryText = new(HdrColor.FromRgb(150, 160, 180));

	public UIElement Root { get; }

	private readonly UIElement _topBar;
	private readonly UIElement _bottomBar;
	private readonly Border _debugPanelBorder;
	private readonly StackPanel _debugPanelBody;
	private readonly StackPanel _hostToggles;
	private readonly Grid _centerSlot;
	private readonly TextBlock _demoNameText;
	private readonly TextBlock _engineText;
	private readonly TextBlock _fpsText;
	private readonly TextBlock _statusText;
	private readonly TextBlock _statusRightText;
	private UIElement? _demoDebugContent;
	private UIElement? _centerContent;
	private bool _panelExpanded = true;
	private bool _hudVisible = true;
	private Button _pinButton = null!;

	public DemoShell(IReadOnlyList<string> demoNames, Action<int> onSelectDemo)
	{
		var root = new Grid();
		root.RowDefinitions.Add(GridLength.Auto);
		root.RowDefinitions.Add(GridLength.Star);
		root.RowDefinitions.Add(GridLength.Auto);
		Root = root;

		_topBar = BuildTopBar(demoNames, onSelectDemo, out _demoNameText, out _engineText, out _fpsText);
		Grid.SetRow(_topBar, 0);
		root.Children.Add(_topBar);

		var middle = new Grid();
		Grid.SetRow(middle, 1);
		root.Children.Add(middle);

		_debugPanelBorder = BuildDebugPanel(out _debugPanelBody, out _hostToggles);
		middle.Children.Add(_debugPanelBorder);

		_centerSlot = new Grid { HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center };
		middle.Children.Add(_centerSlot);

		_bottomBar = BuildBottomBar(out _statusText, out _statusRightText);
		Grid.SetRow(_bottomBar, 2);
		root.Children.Add(_bottomBar);

		UpdatePanelVisibility(); // starts collapsed — nothing has been added yet
	}

	private UIElement BuildTopBar(IReadOnlyList<string> demoNames, Action<int> onSelectDemo, out TextBlock demoNameText, out TextBlock engineText, out TextBlock fpsText)
	{
		var bar = new Grid { Background = _barBackground };
		bar.ColumnDefinitions.Add(GridLength.Auto); // menu
		bar.ColumnDefinitions.Add(GridLength.Auto); // demo name
		bar.ColumnDefinitions.Add(GridLength.Star); // spacer
		bar.ColumnDefinitions.Add(GridLength.Auto); // engine
		bar.ColumnDefinitions.Add(GridLength.Auto); // fps

		var menuBar = new MenuBar();
		var demosMenu = new MenuBarItem { Title = "Demos" };
		for (var i = 0; i < demoNames.Count; i++)
		{
			var index = i; // capture by value
			var item = new MenuFlyoutItem { Text = demoNames[i] };
			item.Click += (_, _) => onSelectDemo(index);
			demosMenu.Items.Add(item);
		}
		menuBar.Children.Add(demosMenu);
		Grid.SetColumn(menuBar, 0);
		bar.Children.Add(menuBar);

		// A right margin too (not just left): the trailing Star spacer column has no children in it, and an empty
		// Star track measures to zero width in practice, so without this the name would run straight into the engine
		// text with no gap.
		demoNameText = new TextBlock { FontSize = 15f, Foreground = _primaryText, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(12f, 0f, 24f, 0f) };
		Grid.SetColumn(demoNameText, 1);
		bar.Children.Add(demoNameText);

		engineText = new TextBlock { FontSize = 12f, Foreground = _secondaryText, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0f, 0f, 16f, 0f) };
		Grid.SetColumn(engineText, 3);
		bar.Children.Add(engineText);

		fpsText = new TextBlock { FontSize = 12f, Foreground = _secondaryText, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0f, 0f, 12f, 0f) };
		Grid.SetColumn(fpsText, 4);
		bar.Children.Add(fpsText);

		return bar;
	}

	private Border BuildDebugPanel(out StackPanel body, out StackPanel hostToggles)
	{
		var header = new Grid();
		header.ColumnDefinitions.Add(GridLength.Star);
		header.ColumnDefinitions.Add(GridLength.Auto);
		var title = new TextBlock { Text = "Debug", FontSize = 14f, Foreground = _accentText, VerticalAlignment = VerticalAlignment.Center };
		Grid.SetColumn(title, 0);
		header.Children.Add(title);
		// ASCII only — the en-dash glyph was missing from the font atlas on at least one platform (rendered as a
		// blank/tofu box), so a plain hyphen-minus is used instead.
		_pinButton = new Button { Content = "-" };
		_pinButton.Click += (_, _) => TogglePanel();
		Grid.SetColumn(_pinButton, 1);
		header.Children.Add(_pinButton);

		hostToggles = new StackPanel { Margin = new Thickness(0f, 8f, 0f, 0f) };
		body = new StackPanel();
		body.Children.Add(hostToggles);

		var stack = new StackPanel();
		stack.Children.Add(header);
		stack.Children.Add(body);

		return new Border
		{
			Margin = new Thickness(12f, 12f, 0f, 0f),
			Padding = new Thickness(16f, 12f),
			HorizontalAlignment = HorizontalAlignment.Left,
			VerticalAlignment = VerticalAlignment.Top,
			Background = _panelBackground,
			BorderBrush = _accentText,
			BorderThickness = new Thickness(1f),
			CornerRadius = new CornerRadius(10f),
			MinWidth = 260f,
			Child = stack,
		};
	}

	private UIElement BuildBottomBar(out TextBlock statusText, out TextBlock statusRightText)
	{
		var bar = new Grid { Background = _barBackground };
		bar.ColumnDefinitions.Add(GridLength.Star);
		bar.ColumnDefinitions.Add(GridLength.Auto);

		statusText = new TextBlock { FontSize = 12f, Foreground = _secondaryText, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(12f, 4f) };
		Grid.SetColumn(statusText, 0);
		bar.Children.Add(statusText);

		statusRightText = new TextBlock { FontSize = 12f, Foreground = _accentText, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(12f, 4f) };
		Grid.SetColumn(statusRightText, 1);
		bar.Children.Add(statusRightText);

		return bar;
	}

	private void TogglePanel()
	{
		_panelExpanded = !_panelExpanded;
		_debugPanelBody.Visibility = _panelExpanded ? Visibility.Visible : Visibility.Collapsed;
		_pinButton.Content = _panelExpanded ? "-" : "+";
	}

	/// <summary>Add a host-owned checkbox toggle (e.g. "Pause camera", "GPU timing") to the debug panel, above
	/// the active demo's own controls. Called once per toggle at shell construction.</summary>
	public void AddHostToggle(string label, Func<bool> get, Action<bool> set)
	{
		var box = new CheckBox { Content = label, IsChecked = get(), Margin = new Thickness(0f, 0f, 0f, 4f) };
		box.Checked += (_, _) => set(true);
		box.Unchecked += (_, _) => set(false);
		_hostToggles.Children.Add(box);
		UpdatePanelVisibility();
	}

	/// <summary>Add a host-owned action button (e.g. "Export graph") to the debug panel. Called once per action at
	/// shell construction.</summary>
	public void AddHostAction(string label, Action onClick)
	{
		var button = new Button { Content = label, Margin = new Thickness(0f, 0f, 0f, 4f), HorizontalAlignment = HorizontalAlignment.Left };
		button.Click += (_, _) => onClick();
		_hostToggles.Children.Add(button);
		UpdatePanelVisibility();
	}

	/// <summary>Hide (or restore) the top bar and the debug panel — the <c>--no-hud</c> chrome-free capture. Any
	/// demo-hosted centre content (the UI-track demos' own page) is unaffected: it is the demo's subject, not chrome.</summary>
	public void SetHudVisible(bool visible)
	{
		_hudVisible = visible;
		_topBar.Visibility = visible ? Visibility.Visible : Visibility.Collapsed;
		_bottomBar.Visibility = visible ? Visibility.Visible : Visibility.Collapsed;
		UpdatePanelVisibility();
	}

	/// <summary>Swap in the active demo's name and its optional debug panel content.</summary>
	public void SetActiveDemo(string name, UIElement? debugContent)
	{
		_demoNameText.Text = name;
		if (_demoDebugContent is not null)
			_debugPanelBody.Children.RemoveAt(_debugPanelBody.Children.Count - 1);
		_demoDebugContent = debugContent;
		if (debugContent is not null)
			_debugPanelBody.Children.Add(debugContent);
		UpdatePanelVisibility();
	}

	// The debug panel is chrome for controlling something — with no host toggles/actions and no active demo debug
	// content there is nothing in it to control, so it collapses entirely rather than showing an empty title bar.
	private void UpdatePanelVisibility() =>
		_debugPanelBorder.Visibility = _hudVisible && (_hostToggles.Children.Count > 0 || _demoDebugContent is not null)
			? Visibility.Visible : Visibility.Collapsed;

	/// <summary>Swap in the active demo's own centred UI content (its subject, for the UI-track demos), or clear it.</summary>
	public void SetCenterContent(UIElement? content)
	{
		if (ReferenceEquals(_centerContent, content))
			return;
		_centerSlot.Children.Clear();
		_centerContent = content;
		if (content is not null)
			_centerSlot.Children.Add(content);
	}

	public void SetEngineName(string name) => _engineText.Text = name;
	public void SetFps(float fps, float ms) => _fpsText.Text = $"{fps:F0} fps ({ms:F2} ms)";
	public void SetStatus(string status) => _statusText.Text = status;
	public void SetStatusRight(string? status) => _statusRightText.Text = status ?? "";
}
