using System;
using ARMusician.Midi;

internal static class MidiCoreTests
{
    private static int assertions;

    private static void Check(bool condition, string message)
    {
        assertions++;
        if (!condition) throw new Exception(message);
    }

    private static void Near(float expected, float actual, string message)
    {
        Check(Math.Abs(expected - actual) < 0.00001f, message + ": " + actual);
    }

    private static void Reject(Action action, string message)
    {
        bool rejected = false;
        try { action(); } catch (ArgumentException) { rejected = true; }
        Check(rejected, message);
    }

    private static void Notes()
    {
        string[] names = { "C-1", "C4", "Db4", "c#4", "Bb3", "Cb4", "B#3", "G9", " F4 " };
        int[] expected = { 0, 60, 61, 61, 58, 59, 60, 127, 65 };
        for (int i = 0; i < names.Length; i++)
        {
            int note;
            Check(MidiNotes.TryParse(names[i], out note) && note == expected[i], "Parse " + names[i]);
        }
        string[] bad = { null, "", " ", "C", "H4", "C##4", "Dbb4", "4C", "C 4", "C+4", "C4.0", "C--1", "Cb-1", "G#9", "C10", "C-2", "C9999999999999", "C4junk" };
        foreach (string name in bad)
        {
            int note;
            Check(!MidiNotes.TryParse(name, out note) && note == -1, "Reject malformed/out-of-range name " + name);
        }
        Check(MidiNotes.Format(0) == "C-1", "Lowest note format");
        Check(MidiNotes.Format(60) == "C4", "Middle C convention");
        Check(MidiNotes.Format(61) == "C#4", "Canonical accidental");
        Check(MidiNotes.Format(127) == "G9", "Highest note format");
        for (int i = 0; i < 128; i++)
        {
            int parsed;
            Check(MidiNotes.TryParse(MidiNotes.Format(i), out parsed) && parsed == i, "Full MIDI name round trip " + i);
        }
        Reject(delegate { MidiNotes.Format(-1); }, "Reject format below MIDI range");
        Reject(delegate { MidiNotes.Format(128); }, "Reject format above MIDI range");
    }

    private static void Geometry()
    {
        KeyGeometry[] keys = KeyboardLayout.Build(36, 61, 2f);
        Check(keys.Length == 61 && keys[0].Note == 36 && keys[60].Note == 96, "All 61 keys including highest C");
        Near(-0.5f, keys[0].Center - keys[0].Width / 2, "Left outer edge");
        Near(0.5f, keys[60].Center + keys[60].Width / 2, "Right outer edge");
        Near(1f / 36f, keys[0].Width, "36 equally wide white keys in five octaves plus C");
        Near((keys[0].Center + keys[2].Center) / 2, keys[1].Center, "C sharp overlays boundary between C and D");
        Near(keys[0].Width / 2, keys[1].Width, "Black width respects ratio");
        Near(keys[2].Center - keys[0].Center, keys[12].Center - keys[11].Center, "B-C and C-D white spacing equal");
        int whiteCount = 0;
        for (int i = 0; i < keys.Length; i++)
        {
            Check(keys[i].Note == 36 + i, "Chromatic ordering " + i);
            Check(keys[i].Center - keys[i].Width / 2 >= -0.50001f && keys[i].Center + keys[i].Width / 2 <= 0.50001f, "Every key within calibrated bounds " + i);
            if (!MidiNotes.IsBlack(keys[i].Note)) whiteCount++;
        }
        Check(whiteCount == 36, "Correct white key count");
        KeyGeometry[] one = KeyboardLayout.Build(60, 1, 1.5f);
        Near(0, one[0].Center, "Single white key centered");
        Near(1, one[0].Width, "Single key fills range");
        Reject(delegate { KeyboardLayout.Build(37, 61, 2); }, "Reject black low endpoint");
        Reject(delegate { KeyboardLayout.Build(36, 62, 2); }, "Reject black high endpoint");
        Reject(delegate { KeyboardLayout.Build(100, 61, 2); }, "Reject overflowing range");
        Reject(delegate { KeyboardLayout.Build(36, 0, 2); }, "Reject empty range");
        Reject(delegate { KeyboardLayout.Build(36, 61, Single.NaN); }, "Reject NaN width ratio");
        Reject(delegate { KeyboardLayout.Build(36, 61, 0); }, "Reject zero width ratio");
    }

    private static void Calibration()
    {
        KeyboardCalibration calibration = new KeyboardCalibration();
        Check(calibration.FirstNote == -1 && calibration.LastNote == -1 && !calibration.IsComplete, "Initial uncalibrated state");
        Check(!calibration.Observe(37) && calibration.FirstNote == -1 && calibration.Error != null, "Black initial endpoint rejected");
        Check(!calibration.Observe(-1) && calibration.FirstNote == -1, "Invalid low MIDI rejected");
        Check(!calibration.Observe(36) && calibration.FirstNote == 36 && calibration.Error == null, "Low endpoint accepted, awaiting high");
        Check(!calibration.Observe(84) && calibration.FirstNote == 36 && calibration.LastNote == -1, "Wrong octave stays incomplete, retains low");
        Check(!calibration.Observe(97) && calibration.Error != null && calibration.FirstNote == 36, "Black high rejected, retains low");
        Check(!calibration.Observe(128) && calibration.FirstNote == 36, "Out-of-range high rejected");
        Check(!calibration.Observe(24), "Reversed endpoint rejected");
        Check(calibration.Observe(96) && calibration.IsComplete && calibration.LastNote == 96 && calibration.Error == null, "Correct high completes after retries");
        calibration.Observe(60);
        Check(calibration.FirstNote == 36 && calibration.LastNote == 96, "Completed calibration not overwritten by playing");
        calibration.Begin();
        Check(!calibration.IsComplete && calibration.FirstNote == -1 && calibration.LastNote == -1, "Restart clears endpoints");
        calibration.Observe(48);
        Check(calibration.Observe(108), "Different explicit 61-note octave range accepted");
        Reject(delegate { new KeyboardCalibration(0); }, "Reject invalid expected count");
    }

    private static void PhysicalHeldNotes()
    {
        HeldNotes held = new HeldNotes();
        held.NoteOn(0, 60);
        held.NoteOn(1, 60);
        held.NoteOn(1, 64);
        held.NoteOff(0, 60);
        Check(held.IsHeld(60), "Releasing one channel preserves another channel's same note");
        held.ResetChannel(0);
        Check(held.IsHeld(60), "Reset unrelated channel preserves note");
        held.NoteOff(1, 60);
        Check(!held.IsHeld(60) && held.IsHeld(64), "Release affects only target note");
        held.Set(15, 127, true);
        held.ResetChannel(1);
        Check(!held.IsHeld(64) && held.IsHeld(127), "Channel reset preserves other channels");
        held.Reset();
        Check(!held.IsHeld(127), "Reset clears all channels");
        held.NoteOn(0, 60);
        held.NoteOn(0, 60);
        held.NoteOff(0, 60);
        Check(!held.IsHeld(60), "Repeated note on is physical state, not a reference count");
        Reject(delegate { held.Set(16, 60, true); }, "Reject invalid channel");
        Reject(delegate { held.IsHeld(128); }, "Reject invalid held note");
    }

    private static void Matching()
    {
        PracticeMatcher matcher = new PracticeMatcher();
        matcher.Add(60, 10);
        matcher.Add(64, 10);
        matcher.Add(67, 10);
        Check(matcher.Hit(67, 10.25, 0.25), "Chord high note at late inclusive boundary");
        Check(matcher.Hit(60, 9.75, 0.25), "Chord low note at early inclusive boundary");
        Check(matcher.Hit(64, 10, 0.25), "Chord order independent");
        Check(!matcher.Hit(60, 10, 0.25) && matcher.Wrong == 1, "Matched event cannot be consumed twice");
        Check(matcher.Hits == 3 && matcher.Total == 3 && matcher.Misses == 0, "Chord counters");
        matcher.Clear();
        matcher.Add(60, 10);
        matcher.Add(60, 10.25);
        Check(matcher.Hit(60, 10.1875, 0.25), "Repeated note consumes nearest event");
        Check(matcher.Hit(60, 9.75, 0.25), "Earlier repeated event remains available after nearest match");
        Check(matcher.Hits == 2, "Both repeated notes counted separately");
        matcher.Clear();
        matcher.Add(60, 2);
        matcher.Add(64, 1);
        matcher.Add(67, 1);
        Check(matcher.Advance(1.25, 0.25) == 0, "Boundary not prematurely expired");
        Check(matcher.Advance(1.2501, 0.25) == 2 && matcher.Misses == 2, "All overdue chord notes expire once even if unsorted");
        Check(matcher.Advance(1.5, 0.25) == 0 && matcher.Misses == 2, "Misses do not repeat");
        Check(!matcher.Hit(60, 1.5, 0.25), "Too early press is wrong and does not consume scheduled event");
        Check(matcher.Hit(60, 2, 0.25), "Future event remains after early wrong press");
        Check(matcher.Hits + matcher.Misses == matcher.Total, "Resolved event accounting invariant");
        matcher.Clear();
        matcher.Add(60, 0);
        Check(!matcher.Hit(60, 1, 0.25) && matcher.Misses == 1 && matcher.Wrong == 1, "Late strike expires event and records wrong press");
        matcher.Clear();
        matcher.Add(60, 1);
        Check(matcher.Hit(60, 1, 0), "Zero tolerance permits exact timestamp");
        matcher.Clear();
        matcher.Add(60, 10);
        matcher.Add(64, 10);
        Check(matcher.Hit(60, 10.2, 0.2), "Decimal late boundary remains inclusive despite subtraction rounding");
        Check(matcher.Hit(64, 9.8, 0.2), "Decimal early boundary remains inclusive despite subtraction rounding");
        matcher.Clear();
        Check(matcher.Total == 0 && matcher.Hits == 0 && matcher.Misses == 0 && matcher.Wrong == 0, "Clear resets entire score");
        Check(matcher.Advance(100, 0.25) == 0, "Clear removes pending events");
        Reject(delegate { matcher.Add(128, 1); }, "Reject invalid scheduled note");
        Reject(delegate { matcher.Add(60, Double.NaN); }, "Reject NaN due time");
        Reject(delegate { matcher.Hit(60, Double.PositiveInfinity, 0.25); }, "Reject nonfinite hit time");
        Reject(delegate { matcher.Advance(1, -1); }, "Reject negative tolerance");
    }

    public static int Main()
    {
        try
        {
            Notes();
            Geometry();
            Calibration();
            PhysicalHeldNotes();
            Matching();
            Console.WriteLine("PASS: " + assertions + " MIDI core assertions");
            return 0;
        }
        catch (Exception error)
        {
            Console.Error.WriteLine("FAIL after " + assertions + " assertions: " + error);
            return 1;
        }
    }
}
