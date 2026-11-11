using System.Collections.Generic;
using ARMusician.Midi;
using UnityEngine;

/// <summary>Quest speaker/headphone guide generated locally from MIDI, independent of the user's keyboard.</summary>
[RequireComponent(typeof(AudioSource))]
public sealed class MidiReferenceAudio : MonoBehaviour
{
    private volatile ReferencePianoSynth synth;
    private AudioSource output;
    private AudioClip carrier;
    private bool soundEnabled = true;
    private bool transportPlaying;
    private double transportTime = -1;

    public bool SoundEnabled
    {
        get { return soundEnabled; }
        set { soundEnabled = value; if (synth != null) synth.Sync(transportTime, transportPlaying && value && isActiveAndEnabled); }
    }

    private void Awake() { EnsureOutput(); }

    private void EnsureOutput()
    {
        if (synth != null) return;
        int rate = AudioSettings.outputSampleRate;
        if (rate < 8000) rate = 48000;
        synth = new ReferencePianoSynth(rate);
        output = GetComponent<AudioSource>();
        if (output == null) output = gameObject.AddComponent<AudioSource>();
        output.playOnAwake = false;
        output.spatialBlend = 0;
        output.volume = 0.8f;
        output.pitch = 1;
        output.loop = true;
        output.mute = false;
        // A short silent carrier keeps Unity's audio filter active; no soundfont or recording.
        carrier = AudioClip.Create("MIDI piano reference", 1024, 1, rate, false);
        output.clip = carrier;
        output.Play();
    }

    /// <summary>Raw, unscaled times/durations. Must run before gameplay mutates the notes.</summary>
    public void BeginSong(List<NoteEvent> notes, float speed)
    {
        EnsureOutput();
        var snapshot = new List<ReferencePianoNote>(notes == null ? 0 : notes.Count);
        if (notes != null) foreach (NoteEvent note in notes)
        {
            if (note == null) continue;
            int pitch = note.midiNumber >= 0 ? note.midiNumber : note.keyIndex;
            snapshot.Add(new ReferencePianoNote(pitch, note.time, note.duration));
        }
        synth.SetSong(snapshot, speed);
        transportTime = -1;
        transportPlaying = false;
        if (!output.isPlaying) output.Play();
    }

    public void Sync(double songTime, bool playing)
    {
        transportTime = songTime;
        transportPlaying = playing;
        if (synth != null) synth.Sync(songTime, playing && soundEnabled && isActiveAndEnabled);
    }

    public void Stop()
    {
        transportPlaying = false;
        transportTime = -1;
        if (synth != null) synth.Stop();
    }

    private void OnAudioFilterRead(float[] data, int channels)
    {
        ReferencePianoSynth renderer = synth;
        if (renderer != null) renderer.Render(data, channels);
    }

    private void OnDisable() { Stop(); }
    private void OnDestroy()
    {
        Stop();
        if (output != null) output.Stop();
        if (carrier != null) Destroy(carrier);
    }
}
