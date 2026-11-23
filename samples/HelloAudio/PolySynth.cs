using BinaryVibrance.Sirrow;

namespace HelloAudio;

/// <summary>
/// An eight-voice subtractive synthesizer written as an <see cref="AudioSource"/>: two detuned band-limited saws a voice,
/// an ADSR envelope, and a resonant low-pass over the lot, with a built-in arpeggiator that plays a chord progression on
/// the engine's sample clock. The game plays it through <see cref="Notes"/> and steers it through the
/// <see cref="AudioParameter"/>s; everything in <see cref="Render"/> runs on the audio thread and allocates nothing.
/// </summary>
internal sealed class PolySynth : AudioSource
{
	private const int VoiceCount = 8;
	private const float Detune = 1.003f;

	// The arpeggiator: A minor, F, C, G, two bars each, as sixteenths up and down two octaves of the chord
	private static readonly int[] Roots = [57, 53, 60, 55];
	private static readonly int[][] Chords = [[0, 3, 7], [0, 4, 7], [0, 4, 7], [0, 4, 7]];
	private const double Bpm = 132;

	private readonly Voice[] _voices = new Voice[VoiceCount];
	private float _sampleRate;
	private float _attackStep;
	private float _decayFactor;
	private float _releaseFactor;
	private const float Sustain = 0.6f;

	// The filter's state (a topology-preserving state-variable filter, stable as its cutoff moves)
	private float _ic1;
	private float _ic2;

	private long _stepFrames;
	private long _nextStep = -1;
	private int _step;
	private int _arpNote = -1;
	private long _arpNoteOff;

	/// <summary>Keys going down and up, from the game.</summary>
	public AudioEventQueue<NoteEvent> Notes { get; } = new(256);

	/// <summary>The low-pass cutoff, in Hz.</summary>
	public AudioParameter Cutoff { get; } = new(2400f);

	/// <summary>The filter's resonance, 0 (none) to just under 1 (ringing).</summary>
	public AudioParameter Resonance { get; } = new(0.35f);

	/// <summary>Above 0.5, the arpeggiator plays.</summary>
	public AudioParameter Arpeggio { get; } = new(0f);

	protected override void OnStart(int sampleRate)
	{
		_sampleRate = sampleRate;
		_attackStep = 1f / (0.005f * sampleRate);                 // 5 ms attack
		_decayFactor = MathF.Exp(-1f / (0.25f * sampleRate));     // 250 ms decay time constant
		_releaseFactor = MathF.Exp(-1f / (0.18f * sampleRate));   // 180 ms release time constant
		_stepFrames = (long)(sampleRate * 60.0 / Bpm / 4.0);      // a sixteenth
	}

	protected override bool Render(Span<float> left, Span<float> right, in AudioRenderInfo info)
	{
		while (Notes.TryTake(out var note))
		{
			if (note.On)
				NoteOn(note.Note, note.Velocity);
			else
				NoteOff(note.Note);
		}

		var arpeggio = Arpeggio.Snap() > 0.5f;
		if (!arpeggio && _arpNote >= 0)
		{
			NoteOff(_arpNote);
			_arpNote = -1;
			_nextStep = -1;
		}

		// The filter's coefficients once a block, at the block's end value: a block is a few milliseconds, too short to hear the steps
		var cutoff = Cutoff.Ramp(info.Frames);
		var cutoffHz = Math.Clamp(cutoff.Value + cutoff.Step * info.Frames, 20f, _sampleRate * 0.45f);
		var g = MathF.Tan(MathF.PI * cutoffHz / _sampleRate);
		var k = 2f - 2f * Math.Clamp(Resonance.Snap(), 0f, 0.98f);
		var a1 = 1f / (1f + g * (g + k));
		var a2 = g * a1;
		var a3 = g * a2;

		for (var i = 0; i < info.Frames; i++)
		{
			if (arpeggio)
				StepArpeggio(info.FrameTime + i);

			var sum = 0f;
			for (var v = 0; v < VoiceCount; v++)
			{
				ref var voice = ref _voices[v];
				if (voice.Stage == Stage.Idle)
					continue;
				sum += voice.Next(this) * voice.Velocity;
			}

			// Low-pass the voices together
			var v3 = sum * 0.18f - _ic2;
			var v1 = a1 * _ic1 + a2 * v3;
			var v2 = _ic2 + a2 * _ic1 + a3 * v3;
			_ic1 = 2f * v1 - _ic1;
			_ic2 = 2f * v2 - _ic2;
			left[i] = v2;
			right[i] = v2;
		}

		// Flush the filter's state to zero as it dies away: tiny (denormal) floats are slow on some CPUs
		if (MathF.Abs(_ic1) < 1e-15f)
			_ic1 = 0f;
		if (MathF.Abs(_ic2) < 1e-15f)
			_ic2 = 0f;
		return true;
	}

	private void StepArpeggio(long frame)
	{
		if (_nextStep < 0)
			_nextStep = frame;
		if (_arpNote >= 0 && frame >= _arpNoteOff)
		{
			NoteOff(_arpNote);
			_arpNote = -1;
		}
		if (frame < _nextStep)
			return;

		var bar = _step / 16;
		var chord = bar / 2 % Roots.Length;
		var shape = Chords[chord];
		var position = _step % 12;
		var up = position < 6 ? position : 12 - position; // up two octaves of the chord and back down
		var note = Roots[chord] + shape[up % 3] + 12 * (up / 3);
		NoteOn(note, _step % 4 == 0 ? 0.9f : 0.6f);
		_arpNote = note;
		_arpNoteOff = frame + _stepFrames / 2;
		_nextStep += _stepFrames;
		_step++;
	}

	private void NoteOn(int note, float velocity)
	{
		// A free voice, else the quietest: stealing the softest note is the least heard
		var chosen = 0;
		var quietest = float.MaxValue;
		for (var v = 0; v < VoiceCount; v++)
		{
			if (_voices[v].Stage == Stage.Idle)
			{
				chosen = v;
				break;
			}
			if (_voices[v].Level < quietest)
			{
				quietest = _voices[v].Level;
				chosen = v;
			}
		}
		var increment = 440f * MathF.Pow(2f, (note - 69) / 12f) / _sampleRate;
		ref var voice = ref _voices[chosen];
		voice.Note = note;
		voice.Velocity = velocity;
		voice.Increment = increment;
		voice.Stage = Stage.Attack;
	}

	private void NoteOff(int note)
	{
		for (var v = 0; v < VoiceCount; v++)
			if (_voices[v].Note == note && _voices[v].Stage is Stage.Attack or Stage.Decay)
				_voices[v].Stage = Stage.Release;
	}

	// The PolyBLEP correction: rounds off the saw's jump so it doesn't alias into a hiss
	private static float PolyBlep(float t, float dt)
	{
		if (t < dt)
		{
			t /= dt;
			return t + t - t * t - 1f;
		}
		if (t > 1f - dt)
		{
			t = (t - 1f) / dt;
			return t * t + t + t + 1f;
		}
		return 0f;
	}

	private enum Stage : byte
	{
		Idle,
		Attack,
		Decay,
		Release,
	}

	private struct Voice
	{
		public Stage Stage;
		public int Note;
		public float Velocity;
		public float Increment;
		public float Level;
		public float PhaseA;
		public float PhaseB;

		public float Next(PolySynth synth)
		{
			switch (Stage)
			{
				case Stage.Attack:
					Level += synth._attackStep;
					if (Level >= 1f)
					{
						Level = 1f;
						Stage = Stage.Decay;
					}
					break;
				case Stage.Decay:
					Level = Sustain + (Level - Sustain) * synth._decayFactor;
					break;
				case Stage.Release:
					Level *= synth._releaseFactor;
					if (Level < 1e-4f)
					{
						Level = 0f;
						Stage = Stage.Idle;
					}
					break;
			}

			var dtA = Increment;
			var dtB = Increment * Detune;
			var saw = 2f * PhaseA - 1f - PolyBlep(PhaseA, dtA) + 2f * PhaseB - 1f - PolyBlep(PhaseB, dtB);
			PhaseA += dtA;
			if (PhaseA >= 1f)
				PhaseA -= 1f;
			PhaseB += dtB;
			if (PhaseB >= 1f)
				PhaseB -= 1f;
			return saw * 0.5f * Level;
		}
	}
}
