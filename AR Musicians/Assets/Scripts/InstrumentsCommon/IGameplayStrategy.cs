using System;

public interface IGameplayStrategy
{
    /// <summary>
    /// The time (in seconds) the note needs to travel from spawn to hit.
    /// (Replaces 'fallTime')
    /// </summary>
    float ApproachTime { get; }

    /// <summary>
    /// Check if the physical instrument (Plane or Drums) is defined
    /// </summary>
    /// <returns>True if the physical instrument is defined. False otherwise.</returns>
    bool IsInstrumentReady();

    /// <summary>
    /// Called when the generic manager loads the JSON. 
    /// Converts "C4" -> KeyIndex (Piano) or "Left/Right" (Bongo)
    /// </summary>
    /// <param name="note">The note to preprocess as an object of type <see cref="NoteEvent"/>.</param>
    void PreprocessNote(NoteEvent note);

    /// <summary>
    /// Called by the generic manager when it's time to create the visual
    /// </summary>
    /// <param name="note">The note for which to create the visualisation.</param>
    /// <param name="gameSpeed">The speed multiplier for the song.</param>
    void SpawnNote(NoteEvent note, float gameSpeed);

    /// <summary>
    /// Event to tell the manager a note finished (hit or missed) so we can count active objects
    /// </summary>
    event Action OnNoteFinished;
}
