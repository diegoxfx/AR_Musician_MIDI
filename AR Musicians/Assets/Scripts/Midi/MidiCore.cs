using System;
using System.Collections.Generic;
using System.Globalization;

namespace ARMusician.Midi
{
    /// <summary>MIDI note names use scientific pitch notation: middle C is C4 (60).</summary>
    public static class MidiNotes
    {
        private static readonly string[] Names = { "C", "C#", "D", "D#", "E", "F", "F#", "G", "G#", "A", "A#", "B" };

        public static bool TryParse(string text, out int note)
        {
            note = -1;
            if (String.IsNullOrWhiteSpace(text)) return false;
            text = text.Trim();
            int pitch;
            switch (Char.ToUpperInvariant(text[0]))
            {
                case 'C': pitch = 0; break;
                case 'D': pitch = 2; break;
                case 'E': pitch = 4; break;
                case 'F': pitch = 5; break;
                case 'G': pitch = 7; break;
                case 'A': pitch = 9; break;
                case 'B': pitch = 11; break;
                default: return false;
            }
            int index = 1;
            if (index < text.Length && (text[index] == '#' || text[index] == 'b'))
            {
                pitch += text[index] == '#' ? 1 : -1;
                index++;
            }
            if (index >= text.Length) return false;
            // Only a signed decimal octave is accepted, with no internal whitespace.
            for (int i = index; i < text.Length; i++)
            {
                if (i == index && text[i] == '-') continue;
                if (text[i] < '0' || text[i] > '9') return false;
            }
            int octave;
            if (!Int32.TryParse(text.Substring(index), NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture, out octave)) return false;
            long value = ((long)octave + 1) * 12 + pitch;
            if (value < 0 || value > 127) return false;
            note = (int)value;
            return true;
        }

        public static string Format(int note)
        {
            Validate(note);
            return Names[note % 12] + (note / 12 - 1).ToString(CultureInfo.InvariantCulture);
        }

        public static bool IsBlack(int note)
        {
            Validate(note);
            int pitch = note % 12;
            return pitch == 1 || pitch == 3 || pitch == 6 || pitch == 8 || pitch == 10;
        }

        internal static void Validate(int note)
        {
            if (note < 0 || note > 127) throw new ArgumentOutOfRangeException("note", "MIDI notes must be between 0 and 127.");
        }
    }

    public struct KeyGeometry
    {
        public int Note;
        public float Center;
        public float Width;
    }

    public static class KeyboardLayout
    {
        /// <summary>Widths span -0.5..0.5. Black keys overlay white-key boundaries.</summary>
        public static KeyGeometry[] Build(int first, int count, float whiteToBlackRatio)
        {
            MidiNotes.Validate(first);
            if (count < 1 || count > 128 - first) throw new ArgumentOutOfRangeException("count");
            if (Single.IsNaN(whiteToBlackRatio) || Single.IsInfinity(whiteToBlackRatio) || whiteToBlackRatio < 1f)
                throw new ArgumentOutOfRangeException("whiteToBlackRatio", "The white-to-black width ratio must be at least one.");
            if (MidiNotes.IsBlack(first) || MidiNotes.IsBlack(first + count - 1))
                throw new ArgumentException("The keyboard range must begin and end on white keys.");
            int whites = 0;
            for (int i = 0; i < count; i++) if (!MidiNotes.IsBlack(first + i)) whites++;
            float width = 1f / whites;
            KeyGeometry[] keys = new KeyGeometry[count];
            int whiteIndex = 0;
            for (int i = 0; i < count; i++)
            {
                bool black = MidiNotes.IsBlack(first + i);
                keys[i] = new KeyGeometry
                {
                    Note = first + i,
                    Center = -0.5f + (whiteIndex + (black ? 0f : 0.5f)) * width,
                    Width = black ? width / whiteToBlackRatio : width
                };
                if (!black) whiteIndex++;
            }
            return keys;
        }
    }

    /// <summary>Two explicit physical endpoint presses; a rejected high note keeps the low note.</summary>
    public sealed class KeyboardCalibration
    {
        private readonly int expectedCount;
        public int FirstNote { get; private set; }
        public int LastNote { get; private set; }
        public bool IsComplete { get; private set; }
        public string Error { get; private set; }

        public KeyboardCalibration(int expectedCount = 61)
        {
            if (expectedCount < 1 || expectedCount > 128) throw new ArgumentOutOfRangeException("expectedCount");
            this.expectedCount = expectedCount;
            Begin();
        }

        public void Begin()
        {
            FirstNote = -1;
            LastNote = -1;
            IsComplete = false;
            Error = null;
        }

        public bool Observe(int note)
        {
            if (IsComplete) return true;
            if (note < 0 || note > 127)
            {
                Error = "The note must be in the MIDI range 0 to 127.";
                return false;
            }
            if (MidiNotes.IsBlack(note))
            {
                Error = "The keyboard endpoints must be white keys.";
                return false;
            }
            if (FirstNote < 0)
            {
                FirstNote = note;
                Error = null;
                return false;
            }
            if (note - FirstNote + 1 != expectedCount)
            {
                Error = "The high note must be " + (expectedCount - 1).ToString(CultureInfo.InvariantCulture) + " semitones above the low note. Retry the high key, or restart calibration.";
                return false;
            }
            LastNote = note;
            IsComplete = true;
            Error = null;
            return true;
        }
    }

    /// <summary>Physical pressed state, independently tracked on the sixteen MIDI channels (0..15).</summary>
    public sealed class HeldNotes
    {
        private readonly bool[,] held = new bool[16, 128];

        public void NoteOn(int channel, int note) { Set(channel, note, true); }
        public void NoteOff(int channel, int note) { Set(channel, note, false); }

        public void Set(int channel, int note, bool on)
        {
            ValidateChannel(channel);
            MidiNotes.Validate(note);
            held[channel, note] = on;
        }

        public bool IsHeld(int note)
        {
            MidiNotes.Validate(note);
            for (int channel = 0; channel < 16; channel++) if (held[channel, note]) return true;
            return false;
        }

        public void Reset() { Array.Clear(held, 0, held.Length); }

        public void ResetChannel(int channel)
        {
            ValidateChannel(channel);
            for (int note = 0; note < 128; note++) held[channel, note] = false;
        }

        private static void ValidateChannel(int channel)
        {
            if (channel < 0 || channel > 15) throw new ArgumentOutOfRangeException("channel");
        }
    }

    /// <summary>Matches each scheduled note once. Caller supplies one consistent, pause-adjusted clock.</summary>
    public sealed class PracticeMatcher
    {
        private sealed class ExpectedNote
        {
            public int Note;
            public double DueTime;
        }

        private readonly List<ExpectedNote> pending = new List<ExpectedNote>();
        public int Hits { get; private set; }
        public int Misses { get; private set; }
        public int Wrong { get; private set; }
        public int Total { get; private set; }

        public void Add(int note, double dueTime)
        {
            MidiNotes.Validate(note);
            ValidateTime(dueTime, "dueTime");
            pending.Add(new ExpectedNote { Note = note, DueTime = dueTime });
            Total++;
        }

        public bool Hit(int note, double now, double tolerance)
        {
            MidiNotes.Validate(note);
            Advance(now, tolerance);
            int nearest = -1;
            double distance = Double.PositiveInfinity;
            for (int i = 0; i < pending.Count; i++)
            {
                double delta = Math.Abs(pending[i].DueTime - now);
                if (pending[i].Note == note && now >= pending[i].DueTime - tolerance &&
                    now <= pending[i].DueTime + tolerance && delta < distance)
                {
                    nearest = i;
                    distance = delta;
                }
            }
            if (nearest < 0)
            {
                Wrong++;
                return false;
            }
            pending.RemoveAt(nearest);
            Hits++;
            return true;
        }

        /// <returns>The number of newly expired scheduled notes.</returns>
        public int Advance(double now, double tolerance)
        {
            ValidateTime(now, "now");
            ValidateTime(tolerance, "tolerance");
            if (tolerance < 0) throw new ArgumentOutOfRangeException("tolerance");
            int expired = 0;
            for (int i = pending.Count - 1; i >= 0; i--)
            {
                // Both tolerance endpoints are eligible; expiration starts strictly afterward.
                if (pending[i].DueTime + tolerance < now)
                {
                    pending.RemoveAt(i);
                    expired++;
                }
            }
            Misses += expired;
            return expired;
        }

        public void Clear()
        {
            pending.Clear();
            Hits = Misses = Wrong = Total = 0;
        }

        private static void ValidateTime(double time, string parameter)
        {
            if (Double.IsNaN(time) || Double.IsInfinity(time)) throw new ArgumentOutOfRangeException(parameter);
        }
    }
}
