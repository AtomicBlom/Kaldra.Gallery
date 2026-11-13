using HelloAudio;
using Loom.Audio;
using Loom.Core.Graph;
using Loom.Core.Hosting;
using Loom.Core.Materials;
using Loom.Input;

LoomApplication.Run(new AudioApp(), args);

/// <summary>
/// Loom's audio, whole: a synthesizer you play from the keyboard, a procedural arpeggio running on the audio thread's
/// sample clock, and a clip fired from the game, each on a bus of its own. The keys are laid out like a piano's: A W S E D
/// F T G Y H U J K is an octave from C. Z and X shift the octave, Space starts and stops the arpeggio, Enter fires the
/// clip, and moving the pointer across the window sweeps the filter. The same source runs on the desktop (SDL 3) and,
/// published for browser-wasm, in a browser (an AudioWorklet, after the page's first click or key).
/// </summary>
internal sealed class AudioApp : LoomApplication
{
	private static readonly (Key Key, int Semitone)[] Piano =
	[
		(Key.A, 0), (Key.W, 1), (Key.S, 2), (Key.E, 3), (Key.D, 4), (Key.F, 5), (Key.T, 6),
		(Key.G, 7), (Key.Y, 8), (Key.H, 9), (Key.U, 10), (Key.J, 11), (Key.K, 12),
	];

	private readonly PolySynth _synth = new();
	private readonly int[] _held = new int[Piano.Length]; // the note each key is sounding, so its release ends that note
	private readonly Random _random = new(7);
	private AudioBus _sfx;
	private AudioClip _blip = null!;
	private int _octave = 60;
	private double _sinceReport;

	protected override void Configure(AppOptions options)
	{
		options.Title = "Loom - Hello Audio";
		options.Audio = new AudioOptions(); // the device's own rate and its smallest buffer
	}

	protected override void LoadContent()
	{
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
		Audio.Play(_synth, music, volume: 0.8f);
		if (AppArgs.Contains("--arpeggio"))
			_synth.Arpeggio.Value = 1f; // start playing straight away, for a run nobody is at the keyboard for

		var device = Audio.Device;
		Console.WriteLine($"Hello Audio: {device.Backend} on '{device.Name}', {device.SampleRate} Hz, " +
			$"{device.BufferFrames}-frame buffer ({1000.0 * device.BufferFrames / device.SampleRate:0.0} ms).");
		Console.WriteLine("Play A W S E D F T G Y H U J K; Z/X octave; Space arpeggio; Enter blip; move the pointer for the filter.");
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
			Audio.Play(_blip, _sfx, volume: 0.7f, pitch: 0.75f + _random.NextSingle() * 0.75f, pan: _random.NextSingle() * 1.6f - 0.8f);

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
}
