using BinaryVibrance.Vysara.Animation;
using BinaryVibrance.Vysara.Drawing;
using BinaryVibrance.Vysara.Media;
using BinaryVibrance.Vysara.Media.Imaging;

namespace Kaldra.Gallery.UI;

/// <summary>
/// Code-behind for <c>VectorUIView.xaml</c>. The generator emits the partial half (typed fields, the visual-state
/// storyboard, <c>InitializeComponent</c>); this half gives the image-brush card its picture and starts the draw-on loop.
/// </summary>
internal sealed partial class VectorUIView
{
	public VectorUIView()
	{
		InitializeComponent();
		// One picture, made in code so it is the same texels on every host, shared by the three brushes.
		var swatch = BuildSwatch();
		((ImageBrush)Banner.Background!).ImageSource = swatch;
		((ImageBrush)Avatar.Background!).ImageSource = swatch;
		((ImageBrush)Frame.BorderBrush!).ImageSource = swatch;
	}

	/// <summary>
	/// Start the looping draw-on. Its storyboard runs on the host's animation clock, which the view only reaches once it
	/// is in a live tree, so a host calls this after showing the page; called before that, the state change would only
	/// snap to its end values.
	/// </summary>
	public void StartDrawOn() => VisualStateManager.GoToState(this, "Looping", useTransitions: true);

	// A 96×64 picture: a diagonal blend from teal to magenta under warm horizontal bands, with a checker in one corner.
	// Integer arithmetic only, so every runtime (desktop JIT, NativeAOT, browser WASM) makes the same bytes.
	private static ImageSource BuildSwatch()
	{
		const int Width = 96, Height = 64;
		var rgba = new byte[Width * Height * 4];
		for (var y = 0; y < Height; y++)
			for (var x = 0; x < Width; x++)
			{
				var t = (x * 255 + y * 255) / (Width + Height - 2);
				var r = 40 + (t * 200 / 255);
				var g = 200 - (t * 150 / 255);
				var b = 210 - (t * 40 / 255);
				if ((y / 8) % 2 == 1)
				{
					r = Math.Min(255, r + 40);
					g = Math.Min(255, g + 25);
				}
				if (x < 24 && y < 24 && ((x / 6) + (y / 6)) % 2 == 0)
					(r, g, b) = (245, 245, 235);
				var i = (y * Width + x) * 4;
				rgba[i] = (byte)r;
				rgba[i + 1] = (byte)g;
				rgba[i + 2] = (byte)b;
				rgba[i + 3] = 255;
			}
		return BitmapSource.FromRgba8(Width, Height, rgba);
	}
}
