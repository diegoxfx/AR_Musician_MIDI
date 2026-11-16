using System;
using System.Collections.Generic;
using System.IO;
using ARMusician.Midi;

internal static class ReferencePianoSynthTests
{
    private const int Rate = 48000;
    private static int assertions;
    private static void Check(bool condition, string message)
    { assertions++; if (!condition) throw new Exception(message); }
    private static double Rms(float[] samples)
    {
        double energy = 0;
        foreach (float sample in samples)
        {
            Check(!Single.IsNaN(sample) && !Single.IsInfinity(sample), "Finite audio sample");
            Check(Math.Abs(sample) < 0.8, "Output retains headroom");
            energy += sample * sample;
        }
        return Math.Sqrt(energy / samples.Length);
    }
    private static ReferencePianoSynth Song(params ReferencePianoNote[] notes)
    {
        var synth = new ReferencePianoSynth(Rate);
        synth.SetSong(notes, 1);
        return synth;
    }
    private static float[] Render(ReferencePianoSynth synth, int length)
    { var samples = new float[length]; synth.Render(samples, 1); return samples; }
    private static void Playback()
    {
        var synth = Song(new ReferencePianoNote(69, 1, 0.25));
        synth.Sync(-2, true);
        Check(Rms(Render(synth, 1024)) == 0, "No notes in approach/countdown");
        synth.Sync(0.99, true);
        Check(Rms(Render(synth, 1024)) == 0, "No early onset");
        synth.Sync(1, true);
        float[] tone = Render(synth, 4800);
        Check(Rms(tone) > 0.02, "Audible MIDI note");
        // Positive zero crossings remain close to 440Hz for the fundamental, rather than changing with speed.
        double fundamental = 0, wrongPitch = 0;
        for (int i = 0; i < tone.Length; i++)
        { fundamental += tone[i] * Math.Sin(2 * Math.PI * 440 * i / Rate); wrongPitch += tone[i] * Math.Sin(2 * Math.PI * 500 * i / Rate); }
        Check(Math.Abs(fundamental) > Math.Abs(wrongPitch) * 8, "Expected A4 pitch");
        synth.Sync(1, true); // Learning freezes the timeline at an onset.
        Render(synth, Rate);
        synth.Sync(1, true);
        Check(Rms(Render(synth, 1024)) == 0, "Frozen onset never retriggers");
    }
    private static void Transport()
    {
        var synth = Song(new ReferencePianoNote(60, 0, 2), new ReferencePianoNote(72, 10, 0.1));
        synth.Sync(0.2, true);
        Check(Rms(Render(synth, 1024)) > 0.01, "Resume reconstructs held note");
        synth.Sync(0.2, false);
        Render(synth, 1024);
        Check(Rms(Render(synth, 1024)) == 0, "Pause fully mutes after five millisecond ramp");
        synth.Sync(0.2, true);
        Check(Rms(Render(synth, 1024)) > 0.01, "Resume restores note without waiting for a new onset");
        synth.Sync(11, true);
        Check(Rms(Render(synth, 1024)) == 0, "Seek does not replay missed or expired notes");
        synth.Sync(0.5, true);
        Check(Rms(Render(synth, 1024)) > 0.01, "Backward seek reconstructs active note");
        synth.Stop(); Render(synth, 1024);
        Check(Rms(Render(synth, 1024)) == 0, "Quit silences output");
        synth.Sync(Double.NaN, true);
        Check(Rms(Render(synth, 1024)) == 0, "Invalid transport silenced");
    }
    private static void SnapshotSpeedAndDensity()
    {
        var input = new List<ReferencePianoNote> { new ReferencePianoNote(60, 2, 1) };
        var synth = new ReferencePianoSynth(Rate);
        synth.SetSong(input, 2);
        input[0] = new ReferencePianoNote(127, 0, 100);
        synth.Sync(0, true);
        Check(Rms(Render(synth, 1024)) == 0, "Input snapshot unaffected by caller mutation");
        synth.Sync(1.1, true);
        Check(Rms(Render(synth, 1024)) > 0.01, "Speed scales onset");
        synth.Sync(1.7, true);
        Check(Rms(Render(synth, 1024)) == 0, "Speed scales duration once");
        var chord = new List<ReferencePianoNote>();
        for (int i = 0; i < 2000; i++) chord.Add(new ReferencePianoNote(36 + i % 61, 0, 0.1));
        chord.Add(new ReferencePianoNote(-1, 0, 2));
        chord.Add(new ReferencePianoNote(128, 0, 2));
        chord.Add(new ReferencePianoNote(60, Double.NaN, 2));
        synth.SetSong(chord, 1);
        synth.Sync(0, true);
        Check(Rms(Render(synth, 4800)) > 0.01, "Dense chord audible with bounded polyphony");
        synth.Sync(5, true);
        Check(Rms(Render(synth, 1024)) == 0, "Dense seek has no note burst");
        // Only the latest publication is applied, regardless of missed main-thread frames.
        for (int i = 0; i < 10000; i++) synth.Sync(i / 100.0, true);
        synth.Stop();
        Check(Rms(Render(synth, 1024)) == 0, "Mailbox never queues stale playback after stop");
    }
    private static float[] Demo()
    {
        var notes = new List<ReferencePianoNote>();
        int[] melody = { 60, 64, 67, 72, 71, 67, 64, 60 };
        for (int i = 0; i < melody.Length; i++) notes.Add(new ReferencePianoNote(melody[i], i * 0.35, 0.3));
        notes.Add(new ReferencePianoNote(48, 0, 1.2));
        notes.Add(new ReferencePianoNote(55, 1.4, 1.2));
        var synth = new ReferencePianoSynth(Rate);
        synth.SetSong(notes, 1);
        var result = new float[Rate * 4];
        var block = new float[480];
        for (int i = 0; i < result.Length; i += block.Length)
        {
            synth.Sync(i / (double)Rate, true);
            synth.Render(block, 1);
            Array.Copy(block, 0, result, i, block.Length);
        }
        return result;
    }
    private static void WriteWave(string path, float[] samples)
    {
        using (var writer = new BinaryWriter(File.Create(path)))
        {
            writer.Write(System.Text.Encoding.ASCII.GetBytes("RIFF")); writer.Write(36 + samples.Length * 2);
            writer.Write(System.Text.Encoding.ASCII.GetBytes("WAVEfmt ")); writer.Write(16);
            writer.Write((short)1); writer.Write((short)1); writer.Write(Rate); writer.Write(Rate * 2);
            writer.Write((short)2); writer.Write((short)16);
            writer.Write(System.Text.Encoding.ASCII.GetBytes("data")); writer.Write(samples.Length * 2);
            foreach (float sample in samples) writer.Write((short)(sample * Int16.MaxValue));
        }
    }
    public static int Main(string[] args)
    {
        Playback(); Transport(); SnapshotSpeedAndDensity();
        float[] demo = Demo(), repeated = Demo();
        Check(Rms(demo) > 0.01, "Demo contains audible sound");
        for (int i = 0; i < demo.Length; i++) Check(demo[i] == repeated[i], "Deterministic rendering");
        if (args.Length == 1) WriteWave(args[0], demo);
        Console.WriteLine("Reference piano audio: " + assertions + " assertions passed; demo RMS=" + Rms(demo).ToString("F4"));
        return 0;
    }
}
