using BinaryVibrance.Kaldra.Input;

namespace Kaldra.Sandbox;

/// <summary>
/// The top input layer: the sandbox's global developer hotkeys. Number keys 1-9 jump to a demo, Tab cycles to the
/// next, and F toggles the free-fly debug camera. It sits above the UI-glue layers and blocks nothing, so the
/// hotkeys respond even while a UI control has captured the pointer or keyboard. Tab only cycles demos when
/// <paramref name="tabCyclesDemos"/> reports it should — when the interactive UI demo is active Tab belongs to the
/// UI's focus navigation instead, and passes straight through to the layers below.
/// </summary>
internal sealed class SandboxControlLayer(
	int demoCount,
	Action<int> selectDemo,
	Action cycleDemo,
	Action toggleDebugCamera,
	Func<bool> tabCyclesDemos) : IInputLayer
{
	public void Process(ref InputContext ctx)
	{
		var snapshot = ctx.Snapshot;

		// Only the 1-9 number-row keys exist physically, so demos past the ninth are reachable via Tab or the menu.
		var numberKeys = Math.Min(demoCount, 9);
		for (var i = 0; i < numberKeys; i++)
			if (snapshot.Pressed(Key.Digit1 + i))
				selectDemo(i);

		if (snapshot.Pressed(Key.Tab) && tabCyclesDemos())
			cycleDemo();

		if (snapshot.Pressed(Key.F))
			toggleDebugCamera();
	}
}
