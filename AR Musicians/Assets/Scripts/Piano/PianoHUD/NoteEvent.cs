using ARMusician.Midi;

[System.Serializable]
public class NoteEvent
{
    public string key;
    // Canonical MIDI pitch. -1 identifies the original key-name-only song format.
    public int midiNumber = -1;
    // Explicit hand label, when available in the source MIDI track.
    public PianoHand sourceHand;
    [System.NonSerialized] public PianoHand assignedHand;
    [System.NonSerialized] public float visualDuration = -1f;
    public float time; // time when it should hit the plane

    public float duration;

    public int keyIndex;

}

[System.Serializable]
public class NoteEventList
{
    public NoteEvent[] Items;
}
