using System.ComponentModel;
using System.Runtime.CompilerServices;

namespace Loom.Sandbox.UI;

/// <summary>
/// The MU2 interactive demo's view model: a click counter exposed as a <see cref="Status"/> string that raises
/// <see cref="INotifyPropertyChanged"/>. The page binds <c>{x:Bind ViewModel.Status, Mode=OneWay}</c> to a
/// <c>TextBlock</c>, so each <see cref="Bump"/> (driven by the button's <c>Click</c>) updates the label through the
/// generated binding glue — the end-to-end "clickable, data-bound" proof.
/// </summary>
internal sealed class CounterViewModel : INotifyPropertyChanged
{
	private int _count;

	public string Status
	{
		get;
		private set { if (field != value) { field = value; OnChanged(); } }
	} = "Click the button";

	public void Bump()
	{
		_count++;
		Status = _count == 1 ? "Clicked 1 time" : $"Clicked {_count} times";
	}

	public event PropertyChangedEventHandler? PropertyChanged;

	private void OnChanged([CallerMemberName] string? name = null) =>
		PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
}
