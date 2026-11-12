#if UNITY_EDITOR || UNITY_STANDALONE_WIN
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Melanchall.DryWetMidi.Common;
using Melanchall.DryWetMidi.Core;
using UnityEngine;

internal static class DesktopSimulationFixture
{
    internal const string Title = "AR Musician - Repeated Notes QA";

    internal static void Install()
    {
        string folder = Path.Combine(Application.persistentDataPath, "Music");
        string path = Path.Combine(folder, Title + ".mid");
        try
        {
            Directory.CreateDirectory(folder);
            if (File.Exists(path)) return;
            // At the default 120 BPM and 480 PPQN the first three G4 notes
            // are contiguous, while the bass C3 rings underneath all three.
            var left = MakeTrack("Left Hand", (48, 0, 1200), (55, 1320, 1800));
            var right = MakeTrack("Right Hand", (67, 0, 200), (67, 200, 400), (67, 400, 600),
                (64, 720, 1000), (72, 1250, 1500), (71, 1520, 1920));
            var midi = new MidiFile(left, right) { TimeDivision = new TicksPerQuarterNoteTimeDivision(480) };
            midi.Write(path, overwriteFile: false);
            Debug.Log("[Desktop simulator] Installed MIDI fixture " + path);
        }
        catch (Exception error) when (error is IOException || error is UnauthorizedAccessException)
        {
            Debug.LogWarning("[Desktop simulator] Could not install MIDI fixture: " + error.Message);
        }
    }

    private static TrackChunk MakeTrack(string name, params (int Pitch, int On, int Off)[] notes)
    {
        var changes = new List<(int Tick, bool On, int Pitch)>();
        foreach (var note in notes)
        {
            changes.Add((note.On, true, note.Pitch));
            changes.Add((note.Off, false, note.Pitch));
        }
        var events = new List<MidiEvent> { new SequenceTrackNameEvent(name) };
        int last = 0;
        foreach (var change in changes.OrderBy(item => item.Tick).ThenBy(item => item.On))
        {
            var pitch = (SevenBitNumber)change.Pitch;
            MidiEvent note = change.On ? (MidiEvent)new NoteOnEvent(pitch, (SevenBitNumber)96)
                : new NoteOffEvent(pitch, (SevenBitNumber)0);
            note.DeltaTime = change.Tick - last;
            last = change.Tick;
            events.Add(note);
        }
        return new TrackChunk(events);
    }
}
#endif
