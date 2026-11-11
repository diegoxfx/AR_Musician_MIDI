using System;
using System.Collections.Generic;
using ARMusician.Midi;

public static class PianoRollLayoutTests
{
    private static int checks;
    private static void Check(bool value, string message) { checks++; if (!value) throw new Exception(message); }
    private static void Near(float expected, float actual, string message) { Check(Math.Abs(expected - actual) < 0.00001f, message); }
    private static NoteEvent N(int pitch, float time, float duration)
    { return new NoteEvent { keyIndex = pitch, time = time, duration = duration }; }

    public static void Run()
    {
        var first = N(60, 0, 2);
        var repeat = N(60, 0.5f, 1);
        var chord = N(64, 0, 2);
        var unison = N(60, 0, 1);
        var notes = new List<NoteEvent> { repeat, chord, first, unison };
        PianoRollLayout.Prepare(notes);
        Near(0.465f, first.visualDuration, "Long held note leaves a clear gap before a repeat");
        Near(first.visualDuration, unison.visualDuration, "Duplicate track onset uses the same next distinct strike");
        Near(2, chord.visualDuration, "A different pitch is not shortened by a chord");
        Near(1, repeat.visualDuration, "Last note keeps its duration");
        Near(2, first.duration, "Reference audio/MIDI duration is untouched");
        Near(0.5f, repeat.time, "Onset timing is untouched");
        Check(Object.ReferenceEquals(notes[0], repeat), "Preparation does not reorder gameplay source");
        PianoRollLayout.Prepare(notes);
        Near(0.465f, first.visualDuration, "Repeated preparation is stable");

        first = N(60, 1, 1);
        repeat = N(60, 1.01f, 1);
        PianoRollLayout.Prepare(new List<NoteEvent> { first, repeat });
        Check(first.visualDuration > 0 && first.time + first.visualDuration < repeat.time,
            "Fast repeated notes remain visible with a strictly positive gap");
        first = N(60, 0, 0.02f);
        repeat = N(60, 1, 1);
        PianoRollLayout.Prepare(new List<NoteEvent> { first, repeat });
        Near(0.02f, first.visualDuration, "Existing staccato gap is preserved");
        first = N(60, 0, 0);
        PianoRollLayout.Prepare(new List<NoteEvent> { first });
        Check(first.visualDuration > 0 && first.duration == 0, "Zero-length imported strikes get a visible head only");
        PianoRollLayout.Prepare(new List<NoteEvent>());

        int notifications = 0;
        Action changed = () => notifications++;
        AppText.SetLanguage(AppLanguage.English);
        AppText.Changed += changed;
        AppText.SetLanguage(AppLanguage.Spanish);
        Check(AppText.Choose("Right hand", "Mano derecha") == "Mano derecha", "Spanish copy resolves");
        AppText.SetLanguage(AppLanguage.Spanish);
        Check(notifications == 1, "No duplicate language-change notification");
        AppText.SetLanguage(AppLanguage.English);
        Check(AppText.Choose("Left hand", "Mano izquierda") == "Left hand", "English copy resolves");
        AppText.Changed -= changed;
        bool rejected = false;
        try { AppText.SetLanguage((AppLanguage)99); } catch (ArgumentOutOfRangeException) { rejected = true; }
        Check(rejected && AppText.Language == AppLanguage.English, "Invalid language cannot corrupt active state");
        Console.WriteLine("PASS: " + checks + " piano-roll/language assertions.");
    }
}
