using System.Collections;
using System.Collections.Generic;
using UnityEngine;

[System.Serializable]
public class NoteEvent
{
    public string key;
    // Canonical MIDI pitch. -1 identifies the original key-name-only song format.
    public int midiNumber = -1;
    public float time; // time when it should hit the plane

    public float duration;

    public int keyIndex;

}

[System.Serializable]
public class NoteEventList
{
    public NoteEvent[] Items;
}
