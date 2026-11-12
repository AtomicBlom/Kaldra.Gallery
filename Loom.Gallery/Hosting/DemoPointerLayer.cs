using System.Numerics;

using Loom.Input;

using Sui = SUIsei.Input;

namespace Loom.Gallery.Hosting;

/// <summary>
/// The bottom input layer: forwards the frame's surviving pointer stream to the active demo as SUIsei
/// <see cref="Sui.PointerInput"/> events — the channel a demo that owns an in-world panel projects onto its
/// world-space quad. Neutral button events carry no position of their own, so a press or release reuses the latest
/// move's position. A finger stands in for the mouse: the first one down presses where it lands, drags and releases as
/// it lifts, and any other finger meanwhile is ignored. Sitting at the bottom of the stack, it only sees the pointer
/// input and touches the overlay UI did not take, so an overlay control under the cursor or finger suppresses the
/// in-world click just as it does game input.
/// </summary>
internal sealed class DemoPointerLayer(Action<Sui.PointerInput> forward) : IInputLayer
{
	private Vector2 _position;
	// The finger acting as the pointer, or -1 while none is.
	private int _finger = -1;

	public void Process(ref InputContext ctx)
	{
		foreach (ref readonly var e in ctx.Incoming)
		{
			switch (e.Kind)
			{
				case InputEventKind.PointerMove:
					_position = new Vector2(e.X, e.Y);
					forward(Sui.PointerInput.Move(_position));
					break;

				case InputEventKind.MouseButtonDown when e.Button == MouseButton.Left:
					forward(Sui.PointerInput.Press(_position));
					break;

				case InputEventKind.MouseButtonUp when e.Button == MouseButton.Left:
					forward(Sui.PointerInput.Release(_position));
					break;

				case InputEventKind.Touch when e.Phase == TouchPhase.Began && _finger < 0:
					_finger = e.PointerId;
					_position = new Vector2(e.X, e.Y);
					forward(Sui.PointerInput.Move(_position));
					forward(Sui.PointerInput.Press(_position));
					break;

				case InputEventKind.Touch when e.PointerId == _finger && e.Phase is TouchPhase.Moved or TouchPhase.Stationary:
					_position = new Vector2(e.X, e.Y);
					forward(Sui.PointerInput.Move(_position));
					break;

				case InputEventKind.Touch when e.PointerId == _finger && e.Phase is TouchPhase.Ended or TouchPhase.Canceled:
					_finger = -1;
					_position = new Vector2(e.X, e.Y);
					forward(Sui.PointerInput.Move(_position));
					forward(Sui.PointerInput.Release(_position));
					break;
			}
		}
	}
}
