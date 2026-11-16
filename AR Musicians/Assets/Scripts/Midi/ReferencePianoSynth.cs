using System;
using System.Collections.Generic;
using System.Threading;

namespace ARMusician.Midi
{
    /// <summary>Immutable input in original MIDI seconds. This is a reference instrument, not a recording.</summary>
    public struct ReferencePianoNote
    {
        public readonly int Pitch;
        public readonly double Time;
        public readonly double Duration;
        public ReferencePianoNote(int pitch, double time, double duration)
        { Pitch = pitch; Time = time; Duration = duration; }
    }

    /// <summary>
    /// Single main-thread publisher / single audio-thread renderer. Publish only the latest
    /// transport position through a bounded, allocation-free mailbox; stale frames never queue
    /// up into a burst. Render does not allocate, lock, or call Unity APIs.
    /// </summary>
    public sealed class ReferencePianoSynth
    {
        public const int VoiceLimit = 24;
        private const int TableSize = 2048;
        private const double Release = 0.08;
        private const double AudibleAge = 10.0;
        private readonly double sampleRate;
        private readonly float[] sine = new float[TableSize + 1];
        private readonly Voice[] voices = new Voice[VoiceLimit];
        private readonly double[] frequencies = new double[128];
        private readonly double[] decays = new double[4];
        private ReferencePianoNote[] publishingNotes = new ReferencePianoNote[0];
        private ReferencePianoNote[] mailboxNotes;
        private double mailboxTime;
        private bool mailboxPlaying;
        private int mailboxGeneration, sequence, publisherSequence;
        private int publishingGeneration;
        private int appliedSequence = -1, generation = -1;
        private ReferencePianoNote[] notes = new ReferencePianoNote[0];
        private bool wasPlaying;
        private double previousTime;
        private int cursor;
        private double outputGain;
        private bool targetAudible;

        private struct Voice
        {
            public bool Active;
            public double Age, Duration, Step, Phase1, Phase2, Phase3, Phase4;
            public double A1, A2, A3, A4;
        }

        public ReferencePianoSynth(int sampleRate)
        {
            if (sampleRate < 8000 || sampleRate > 192000) throw new ArgumentOutOfRangeException("sampleRate");
            this.sampleRate = sampleRate;
            for (int i = 0; i <= TableSize; i++) sine[i] = (float)Math.Sin(i * 2 * Math.PI / TableSize);
            for (int i = 0; i < frequencies.Length; i++) frequencies[i] = 440 * Math.Pow(2, (i - 69) / 12.0);
            decays[0] = Math.Exp(-1 / (2.4 * sampleRate));
            decays[1] = Math.Exp(-1 / (1.1 * sampleRate));
            decays[2] = Math.Exp(-1 / (0.55 * sampleRate));
            decays[3] = Math.Exp(-1 / (0.28 * sampleRate));
        }

        /// <summary>Main thread only. Copies input before the game mutates note durations.</summary>
        public void SetSong(IList<ReferencePianoNote> source, double speed)
        {
            if (Double.IsNaN(speed) || Double.IsInfinity(speed) || speed <= 0)
                throw new ArgumentOutOfRangeException("speed");
            var valid = new List<ReferencePianoNote>();
            if (source != null) for (int i = 0; i < source.Count; i++)
            {
                ReferencePianoNote n = source[i];
                if (n.Pitch < 0 || n.Pitch > 127 || !Finite(n.Time) || !Finite(n.Duration) || n.Time < 0 || n.Duration < 0) continue;
                double onset = n.Time / speed;
                double duration = Math.Max(0.03, n.Duration / speed);
                if (Finite(onset) && Finite(duration)) valid.Add(new ReferencePianoNote(n.Pitch, onset, duration));
            }
            valid.Sort(delegate(ReferencePianoNote a, ReferencePianoNote b)
            {
                int order = a.Time.CompareTo(b.Time);
                return order != 0 ? order : a.Pitch.CompareTo(b.Pitch);
            });
            publishingNotes = valid.ToArray();
            publishingGeneration++;
            Sync(-1, false);
        }

        public void Sync(double songTime, bool playing)
        {
            if (!Finite(songTime)) { songTime = -1; playing = false; }
            int next = publisherSequence + 2;
            // Full fence prevents payload writes becoming visible before the odd marker.
            Interlocked.Exchange(ref sequence, next - 1);
            mailboxNotes = publishingNotes;
            mailboxGeneration = publishingGeneration;
            mailboxTime = songTime;
            mailboxPlaying = playing;
            Volatile.Write(ref sequence, next);
            publisherSequence = next;
        }

        public void Stop() { Sync(-1, false); }

        private static bool Finite(double value) { return !Double.IsNaN(value) && !Double.IsInfinity(value); }

        private void ReadTransport()
        {
            // A concurrent publication is simply retried next buffer. Never wait on the audio thread.
            int first = Volatile.Read(ref sequence);
            if ((first & 1) != 0 || first == appliedSequence) return;
            ReferencePianoNote[] candidate = mailboxNotes;
            int candidateGeneration = mailboxGeneration;
            double time = mailboxTime;
            bool playing = mailboxPlaying;
            Thread.MemoryBarrier();
            if (first != Volatile.Read(ref sequence)) return;
            appliedSequence = first;
            bool changed = generation != candidateGeneration;
            if (changed)
            {
                notes = candidate ?? notes;
                generation = candidateGeneration;
            }
            targetAudible = playing;
            if (!playing)
            {
                wasPlaying = false;
                previousTime = time;
                return;
            }

            int next = UpperBound(time);
            bool discontinuity = changed || !wasPlaying || time < previousTime || time - previousTime > 0.25 || next - cursor > 128;
            if (discontinuity)
            {
                ClearVoices();
                // Reconstruct only currently audible notes. Bounded work even for pathological files.
                int found = 0;
                for (int i = next - 1, inspected = 0; i >= 0 && inspected < 512 && found < VoiceLimit; i--, inspected++)
                {
                    double age = time - notes[i].Time;
                    if (age > AudibleAge) break;
                    if (age < notes[i].Duration + Release) { StartVoice(notes[i], age); found++; }
                }
            }
            else if (time > previousTime)
            {
                for (int i = cursor; i < next; i++)
                {
                    double age = time - notes[i].Time;
                    if (age < Math.Min(AudibleAge, notes[i].Duration + Release)) StartVoice(notes[i], age);
                }
            }
            cursor = next;
            previousTime = time;
            wasPlaying = true;
        }

        private int UpperBound(double time)
        {
            int lo = 0, hi = notes.Length;
            while (lo < hi)
            {
                int mid = lo + (hi - lo) / 2;
                if (notes[mid].Time <= time) lo = mid + 1; else hi = mid;
            }
            return lo;
        }

        private void ClearVoices() { for (int i = 0; i < voices.Length; i++) voices[i].Active = false; }

        private void StartVoice(ReferencePianoNote note, double age)
        {
            int slot = 0;
            double quietest = Double.MaxValue;
            for (int i = 0; i < voices.Length; i++)
            {
                if (!voices[i].Active) { slot = i; break; }
                if (voices[i].A1 < quietest) { slot = i; quietest = voices[i].A1; }
            }
            double frequency = frequencies[note.Pitch];
            Voice v = new Voice();
            v.Active = true;
            v.Age = age;
            v.Duration = note.Duration;
            v.Step = frequency / sampleRate;
            v.Phase1 = Fraction(age * frequency);
            v.Phase2 = Fraction(age * frequency * 2.001);
            v.Phase3 = Fraction(age * frequency * 3.002);
            v.Phase4 = Fraction(age * frequency * 4.003);
            v.A1 = Math.Exp(-age / 2.4);
            v.A2 = frequency * 2.001 < sampleRate * 0.45 ? 0.45 * Math.Exp(-age / 1.1) : 0;
            v.A3 = frequency * 3.002 < sampleRate * 0.45 ? 0.2 * Math.Exp(-age / 0.55) : 0;
            v.A4 = frequency * 4.003 < sampleRate * 0.45 ? 0.1 * Math.Exp(-age / 0.28) : 0;
            voices[slot] = v;
        }

        private static double Fraction(double value) { return value - Math.Floor(value); }
        private float Wave(double phase)
        {
            double position = phase * TableSize;
            int index = (int)position;
            return sine[index] + (sine[index + 1] - sine[index]) * (float)(position - index);
        }
        private static double Advance(double phase, double step)
        { phase += step; return phase - (int)phase; }

        /// <summary>Fills interleaved PCM (mono duplicated to all output channels).</summary>
        public void Render(float[] output, int channels)
        {
            if (output == null || channels <= 0) return;
            ReadTransport();
            double delta = 1 / sampleRate;
            double ramp = 1 / (0.005 * sampleRate);
            for (int frame = 0; frame < output.Length; frame += channels)
            {
                outputGain = targetAudible ? Math.Min(1, outputGain + ramp) : Math.Max(0, outputGain - ramp);
                double mixed = 0;
                for (int i = 0; i < voices.Length; i++)
                {
                    Voice v = voices[i];
                    if (!v.Active) continue;
                    double release = v.Age <= v.Duration ? 1 : Math.Max(0, 1 - (v.Age - v.Duration) / Release);
                    if (release == 0 || v.Age > AudibleAge) { v.Active = false; voices[i] = v; continue; }
                    double envelope = Math.Min(1, v.Age / 0.006) * release;
                    mixed += (Wave(v.Phase1) * v.A1 + Wave(v.Phase2) * v.A2 + Wave(v.Phase3) * v.A3 + Wave(v.Phase4) * v.A4) * envelope * 0.16;
                    v.Phase1 = Advance(v.Phase1, v.Step);
                    v.Phase2 = Advance(v.Phase2, v.Step * 2.001);
                    v.Phase3 = Advance(v.Phase3, v.Step * 3.002);
                    v.Phase4 = Advance(v.Phase4, v.Step * 4.003);
                    v.A1 *= decays[0]; v.A2 *= decays[1]; v.A3 *= decays[2]; v.A4 *= decays[3];
                    v.Age += delta;
                    voices[i] = v;
                }
                // Soft saturation guarantees headroom for dense chords without hard clipping.
                float sample = (float)(outputGain * 0.8 * mixed / (1 + Math.Abs(mixed)));
                for (int channel = 0; channel < channels && frame + channel < output.Length; channel++) output[frame + channel] = sample;
            }
            if (!targetAudible && outputGain == 0) ClearVoices();
        }
    }
}
