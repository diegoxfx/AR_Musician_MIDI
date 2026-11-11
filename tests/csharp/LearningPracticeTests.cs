using System;
using ARMusician.Midi;

public static class LearningPracticeTests
{
    private static int checks;
    private static void Check(bool value, string message) { checks++; if (!value) throw new Exception(message); }
    private static void Near(double expected, double actual, string message) { Check(Math.Abs(expected - actual) < 0.00001, message + ": " + actual); }
    private static LearningPractice.Strike N(int pitch, double time) { return new LearningPractice.Strike(pitch, time); }
    public static int Main()
    {
        var p = new LearningPractice(new[] { N(60, 1), N(64, 1.012), N(67, 1.019), N(60, 1), N(36, 2), N(96, 3), N(35, 4), N(97, 4) }, 36, 96, -2, 0);
        Check(p.TotalGroups == 3, "Close chord pitches collapse, exact unisons deduplicate, physical boundaries inclusive");
        Near(1, p.Advance(100, true), "Long frame clamps to earliest chord");
        Check(p.Waiting && p.PendingPitches.Length == 3, "Chord gate exposes all required pitches");
        Check(!p.Receive(61, true, 100, true), "Wrong pitch does not satisfy gate");
        Check(p.Receive(60, true, 100, true), "First chord pitch accepted");
        Check(p.Waiting && p.CompletedGroups == 0 && p.PendingPitches.Length == 2, "Partial chord remains gated");
        p.Receive(64, true, 100, true); p.Receive(67, true, 100, true);
        Check(p.CompletedGroups == 1 && !p.Waiting, "All chord pitches release gate");
        Near(1, p.Advance(100, true), "Repeated readers after release cannot reuse wall time");
        Near(1.1, p.Advance(100.1, true), "Release discards waiting duration");
        Near(2, p.Advance(300, true), "Next long frame cannot skip next gate");

        p = new LearningPractice(new[] { N(60, 1), N(60, 1.01), N(64, 1.031) }, 36, 96, 0, 0);
        Check(p.TotalGroups == 3, "Repeated pitches never absorb into chord, chain offsets do not merge groups");
        p.Advance(1, true); p.Receive(60, true, 1, true); p.Advance(1.01, true);
        Check(!p.Receive(60, true, 1.01, true) && p.Waiting, "Held pitch cannot satisfy repeated note");
        p.Receive(60, false, 1.01, true); p.Receive(60, true, 1.01, true);
        Check(p.CompletedGroups == 2, "New note-on after release satisfies repeat");

        p = new LearningPractice(new[] { N(60, 1), N(64, 1) }, 36, 96, 0, 0);
        Check(!p.Receive(60, true, .8, true), "More than 150 ms early is ignored");
        p.Receive(60, false, .8, true);
        Check(p.Receive(60, true, .9, true), "Slight anticipation is accepted");
        p.Receive(64, true, .95, true);
        Check(p.CompletedGroups == 0, "Early chord does not move clock before its onset");
        Near(1, p.Advance(20, true), "Early completed chord consumes only its onset even on long frame");
        Check(p.CompletedGroups == 1, "Anticipated complete chord clears at onset");

        p = new LearningPractice(new[] { N(60, 1), N(64, 1) }, 36, 96, 0, 0);
        p.Advance(1, true); p.Receive(60, true, 1, true); p.ResetInput();
        Check(p.PendingPitches.Length == 2 && p.CompletedGroups == 0, "Reset removes partial credit and keeps current gate");
        Near(1, p.Advance(100, false), "Paused clock cannot advance");
        Check(!p.Receive(60, true, 101, false), "Paused input cannot satisfy gate");
        p.ResetInput(); p.Receive(60, true, 101, true); p.Receive(64, true, 101, true);
        Near(1.1, p.Advance(101.1, true), "Resume consumes no paused interval");

        p = new LearningPractice(new[] { N(60, 1), N(64, 1.015), N(67, 1.03) }, 36, 96, 0, 0);
        Check(p.TotalGroups == 2, "Chord tolerance anchors to first onset instead of chaining");
        p = new LearningPractice(new[] { N(60, 1), N(64, 1.2) }, 61, 72, 0, 0);
        Check(p.TotalGroups == 1, "Calibration range controls playable pitches");
        Console.WriteLine("PASS: " + checks + " learning timeline assertions");
        return 0;
    }
}
