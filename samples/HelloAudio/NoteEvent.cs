namespace HelloAudio;

/// <summary>A key going down or up on <see cref="PolySynth"/>, sent from the game to the audio thread through its queue.</summary>
/// <param name="Note">The MIDI note number: 60 is middle C.</param>
/// <param name="Velocity">How hard, 0..1; a note off ignores it.</param>
/// <param name="On">True for a key going down, false for one coming up.</param>
internal readonly record struct NoteEvent(int Note, float Velocity, bool On);
