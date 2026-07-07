using System.ComponentModel;
using System.Runtime.CompilerServices;

namespace Loom.Web.UI;

/// <summary>
/// The interactive demo's view model: a click counter exposed as a <see cref="Status"/> string that raises
/// <see cref="INotifyPropertyChanged"/>. The page binds <c>{x:Bind ViewModel.Status, Mode=OneWay}</c> so each
/// <see cref="Bump"/> or <see cref="Reset"/> updates the label through the generated binding glue.
/// </summary>
internal sealed class CounterViewModel : INotifyPropertyChanged
{
	private int _count;

	public string Status
	{
		get;
		private set { if (field != value) { field = value; OnChanged(); } }
	} = "Tab → focus  Enter/Space → click";

	public void Bump()
	{
		_count++;
		Status = _count == 1 ? "Incremented 1 time" : $"Incremented {_count} times";
	}

	public void Reset()
	{
		_count = 0;
		Status = "Counter reset";
	}

	public event PropertyChangedEventHandler? PropertyChanged;

	private void OnChanged([CallerMemberName] string? name = null) =>
		PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
}
