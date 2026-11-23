using System.Numerics;
using BinaryVibrance.Sirrow;
using BinaryVibrance.Kaldra.Core.Graph;
using BinaryVibrance.Kaldra.Core.Hosting;
using BinaryVibrance.Kaldra.Core.Materials;
using BinaryVibrance.Kaldra.Input;

namespace HelloAudio;

/// <summary>
/// Kaldra's audio, whole: a synthesizer you play from the keyboard, a procedural arpeggio running on the audio thread's
/// sample clock, and a clip fired from the game, each on a bus of its own. The keys are laid out like a piano's: A W S E D
/// F T G Y H U J K is an octave from C. Z and X shift the octave, Space starts and stops the arpeggio, Enter fires the
/// clip from a random spot around the listener (a 3D voice, panned and quieter with distance), O starts and stops a hum
/// circling the listener (louder as it passes behind, higher in pitch as it comes closer), and moving the pointer across
/// the window sweeps the filter. A click or a finger plays a note too, higher the
/// nearer the top, on an A minor pentatonic so any two sound together. The same source runs on the desktop (SDL 3), on
/// Android (AAudio, where with no keyboard to start it the arpeggio plays from the start), and, published for
/// browser-wasm, in a browser (an AudioWorklet, after the page's first click or key).
/// </summary>
internal sealed class AudioApp : KaldraApplication
{
	private static readonly (Key Key, int Semitone)[] Piano =
	[
		(Key.A, 0), (Key.W, 1), (Key.S, 2), (Key.E, 3), (Key.D, 4), (Key.F, 5), (Key.T, 6),
		(Key.G, 7), (Key.Y, 8), (Key.H, 9), (Key.U, 10), (Key.J, 11), (Key.K, 12),
	];

	// Two octaves of A minor pentatonic up from A3, bottom of the window to top
	private static readonly int[] Pentatonic = [57, 60, 62, 64, 67, 69, 72, 74, 76, 79, 81];

	private readonly PolySynth _synth = new();
	private readonly int[] _held = new int[Piano.Length]; // the note each key is sounding, so its release ends that note
	private readonly Random _random = new(7);
	private AudioBus _sfx;
	private AudioClip _blip = null!;
	private int _octave = 60;
	private int _pointerNote = -1;
	private double _sinceReport;
	// The orbit (O): a hum circling the listener on a circle off to its front, so it comes and goes as well as moving
	// round. The Doppler shift is four times real, enough to hear plainly at this speed
	private const float OrbitRadius = 8f;
	private const float OrbitSeconds = 4f;
	private static readonly Vector3 OrbitCentre = new(0f, 0f, -5f);
	private static readonly AudioSpatial OrbitSpatial = new() { MinDistance = 2f, MaxDistance = 40f, DopplerFactor = 4f };
	private AudioClip _hum = null!;
	private VoiceHandle _orbit;
	private float _orbitAngle;

	// The app's audio, over the output its platform's entry point registered (Platforms/): opened as the app loads,
	// updated once a frame, paused while the app is off screen, quieter out of focus, closed as it unloads
	private AudioEngine Audio { get; set; } = null!;

	protected override void Configure(AppOptions options) => options.Title = "Kaldra - Hello Audio";

	protected override void LoadContent()
	{
		var options = new AudioOptions(); // the device's own rate and its smallest buffer
		Audio = new AudioEngine(AudioOutput.Open(options), options);

		Engine.DefineTechnique("triangle", new TechniqueDescriptor("triangle"));
		SetGraph(RenderGraph.Define(g =>
		{
			var screen = g.Backbuffer;
			g.Pass("triangle")
				.Writes(screen)
				.Draw(static ctx => ctx.FullScreen("triangle"));
			g.Present(screen);
		}));

		var music = Audio.CreateBus("music");
		_sfx = Audio.CreateBus("sfx");
		_blip = Audio.CreateClip(Blip(Audio.SampleRate), 1, Audio.SampleRate);
		_hum = Audio.CreateClip(Hum(Audio.SampleRate), 1, Audio.SampleRate);
		Audio.Play(_synth, music, volume: 0.8f);
		if (AppArgs.Contains("--arpeggio") || OperatingSystem.IsAndroid())
			_synth.Arpeggio.Value = 1f; // start playing straight away, for a run nobody is at a keyboard for

		var device = Audio.Device;
		Console.WriteLine($"Hello Audio: {device.Backend} on '{device.Name}', {device.SampleRate} Hz, " +
			$"{device.BufferFrames}-frame buffer ({1000.0 * device.BufferFrames / device.SampleRate:0.0} ms).");
		Console.WriteLine("Play A W S E D F T G Y H U J K; Z/X octave; Space arpeggio; Enter blip; O orbit; move the pointer for the filter.");
	}

	protected override void Update(FrameTime time)
	{
		for (var i = 0; i < Piano.Length; i++)
		{
			var (key, semitone) = Piano[i];
			if (Input.Pressed(key))
			{
				_held[i] = _octave + semitone;
				_synth.Notes.TryPost(new NoteEvent(_held[i], 0.9f, On: true));
			}
			if (Input.Released(key))
				_synth.Notes.TryPost(new NoteEvent(_held[i], 0f, On: false));
		}

		if (Input.Pressed(Key.Z))
			_octave = Math.Max(24, _octave - 12);
		if (Input.Pressed(Key.X))
			_octave = Math.Min(96, _octave + 12);
		if (Input.Pressed(Key.Space))
			_synth.Arpeggio.Value = _synth.Arpeggio.Value > 0.5f ? 0f : 1f;
		if (Input.Pressed(Key.Enter))
		{
			// Somewhere around the listener (at the origin, facing -Z), 1 to 6 units away: panned and quieter with distance
			var angle = _random.NextSingle() * MathF.Tau;
			var distance = 1f + _random.NextSingle() * 5f;
			var at = new Vector3(MathF.Sin(angle) * distance, 0f, -MathF.Cos(angle) * distance);
			Audio.PlayAt(_blip, at, _sfx, volume: 0.7f, pitch: 0.75f + _random.NextSingle() * 0.75f);
		}

		if (Input.Pressed(MouseButton.Left) && SurfaceSize.Y > 0)
		{
			var height = 1f - Math.Clamp(Input.PointerPosition.Y / SurfaceSize.Y, 0f, 0.999f);
			_pointerNote = Pentatonic[(int)(height * Pentatonic.Length)];
			_synth.Notes.TryPost(new NoteEvent(_pointerNote, 0.9f, On: true));
		}
		if (Input.Released(MouseButton.Left) && _pointerNote >= 0)
		{
			_synth.Notes.TryPost(new NoteEvent(_pointerNote, 0f, On: false));
			_pointerNote = -1;
		}

		if (Input.Pressed(Key.O))
		{
			if (Audio.IsPlaying(_orbit))
			{
				Audio.Stop(_orbit, fadeSeconds: 0.3f);
				_orbit = VoiceHandle.None;
			}
			else
			{
				_orbitAngle = 0f;
				var (position, _) = Orbit(_orbitAngle);
				_orbit = Audio.PlayAt(_hum, position, _sfx, volume: 0.4f, loop: true, spatial: OrbitSpatial);
			}
		}
		if (Audio.IsPlaying(_orbit))
		{
			_orbitAngle += MathF.Tau / OrbitSeconds * (float)time.DeltaSeconds;
			var (position, velocity) = Orbit(_orbitAngle);
			Audio.SetPosition(_orbit, position, velocity);
		}

		// Across the window, 150 Hz to 9.6 kHz: an octave every sixth of the width
		if (Input.PointerDelta != default && SurfaceSize.X > 0)
			_synth.Cutoff.Value = 150f * MathF.Pow(2f, 6f * Math.Clamp(Input.PointerPosition.X / SurfaceSize.X, 0f, 1f));

		if ((_sinceReport += time.DeltaSeconds) >= 2.0)
		{
			_sinceReport = 0;
			var stats = Audio.Stats;
			Console.WriteLine($"audio: {stats.Callbacks} buffers of {stats.LastCallbackFrames} frames, render load {stats.RenderLoad:P1} " +
				$"(peak {stats.MaxRenderLoad:P1}), {stats.ActiveVoices} voices, level {stats.Peak:0.00}");
		}
	}

	// After the frame has started what it plays: frees the voices that ended, and in the browser sends the frame's mix
	protected override void BeforeRender(FrameTime time) => Audio.Update();

	protected override void OnFocusChanged(bool focused) => Audio.SetFocused(focused);

	protected override void OnSuspending() => Audio.Pause();

	protected override void OnResumed() => Audio.Resume();

	protected override void UnloadContent() => Audio.Dispose();

	// A short falling ping: a sine swept down an octave under a fast decay
	private static float[] Blip(int sampleRate)
	{
		var samples = new float[sampleRate * 3 / 20];
		var phase = 0.0;
		for (var i = 0; i < samples.Length; i++)
		{
			var t = (double)i / sampleRate;
			var frequency = 1320.0 * Math.Pow(0.5, t / 0.15);
			phase += 2 * Math.PI * frequency / sampleRate;
			samples[i] = (float)(Math.Sin(phase) * Math.Exp(-t * 28));
		}
		return samples;
	}

	// Where the orbit is at `angle`, and how fast it's going there. It starts straight ahead, at the circle's far side, and
	// goes round clockwise seen from above: right, behind, left
	private static (Vector3 Position, Vector3 Velocity) Orbit(float angle)
	{
		var (sin, cos) = MathF.SinCos(angle);
		var position = OrbitCentre + new Vector3(sin, 0f, -cos) * OrbitRadius;
		var velocity = new Vector3(cos, 0f, sin) * (OrbitRadius * MathF.Tau / OrbitSeconds);
		return (position, velocity);
	}

	// A second of a band-limited sawtooth at 220 Hz: a whole number of cycles, so it loops without a click, and buzzy
	// enough that a shift in pitch is easy to hear
	private static float[] Hum(int sampleRate)
	{
		var samples = new float[sampleRate];
		for (var i = 0; i < samples.Length; i++)
		{
			var t = (double)i / sampleRate;
			var value = 0.0;
			for (var harmonic = 1; harmonic <= 8; harmonic++)
				value += Math.Sin(2 * Math.PI * 220 * harmonic * t) / harmonic;
			samples[i] = (float)(0.4 * value);
		}
		return samples;
	}
}
