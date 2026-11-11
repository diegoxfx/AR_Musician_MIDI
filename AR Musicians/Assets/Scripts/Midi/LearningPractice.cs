using System;
using System.Collections.Generic;

namespace ARMusician.Midi
{
    /// <summary>Pure wait-for-notes timeline. Wall time is consumed once, including while gated.</summary>
    public sealed class LearningPractice
    {
        public struct Strike
        {
            public int Pitch;
            public double Time;
            public Strike(int pitch, double time) { Pitch = pitch; Time = time; }
        }

        private sealed class Group
        {
            public double Time;
            public readonly HashSet<int> Pitches = new HashSet<int>();
        }

        // Small MIDI export/track offsets may represent one chord. Anchor to the
        // FIRST onset (never chain offsets), and never absorb a repeated pitch.
        public const double ChordWindow = 0.020;
        public const double EarlyWindow = 0.150;
        private readonly List<Group> groups = new List<Group>();
        private readonly HashSet<int> credited = new HashSet<int>();
        private readonly HashSet<int> held = new HashSet<int>();
        private double wallTime;
        public double SongTime { get; private set; }
        public int CompletedGroups { get; private set; }
        public int TotalGroups { get { return groups.Count; } }
        public bool Waiting { get { return CompletedGroups < groups.Count && SongTime >= groups[CompletedGroups].Time; } }
        public int[] PendingPitches
        {
            get
            {
                var pending = new List<int>();
                if (CompletedGroups < groups.Count)
                    foreach (int pitch in groups[CompletedGroups].Pitches)
                        if (!credited.Contains(pitch)) pending.Add(pitch);
                pending.Sort();
                return pending.ToArray();
            }
        }

        public LearningPractice(IEnumerable<Strike> source, int firstPitch, int lastPitch, double initialSongTime, double now)
        {
            if (source == null) throw new ArgumentNullException("source");
            if (firstPitch < 0 || lastPitch > 127 || firstPitch > lastPitch) throw new ArgumentOutOfRangeException("firstPitch");
            SongTime = initialSongTime;
            wallTime = now;
            var sorted = new List<Strike>();
            var unisons = new HashSet<Tuple<int, double>>();
            foreach (var strike in source)
            {
                if (Double.IsNaN(strike.Time) || Double.IsInfinity(strike.Time) || strike.Time < 0)
                    throw new ArgumentOutOfRangeException("source");
                if (strike.Pitch >= firstPitch && strike.Pitch <= lastPitch &&
                    unisons.Add(Tuple.Create(strike.Pitch, strike.Time))) sorted.Add(strike);
            }
            sorted.Sort((a, b) => { int order = a.Time.CompareTo(b.Time); return order != 0 ? order : a.Pitch.CompareTo(b.Pitch); });
            foreach (var strike in sorted)
            {
                Group group = groups.Count == 0 ? null : groups[groups.Count - 1];
                if (group == null || strike.Time - group.Time > ChordWindow || group.Pitches.Contains(strike.Pitch))
                { group = new Group { Time = strike.Time }; groups.Add(group); }
                group.Pitches.Add(strike.Pitch);
            }
        }

        public double Advance(double now, bool playing)
        {
            double delta = Math.Max(0, now - wallTime);
            wallTime = now;
            if (!playing) return SongTime;
            double next = SongTime + delta;
            if (CompletedGroups < groups.Count)
            {
                Group group = groups[CompletedGroups];
                SongTime = Math.Min(next, group.Time);
                // Discard overshoot even when all notes were played slightly early.
                // This prevents long frames and later Update consumers skipping a gate.
                if (Waiting && credited.Count == group.Pitches.Count) CompleteGroup();
            }
            else SongTime = next;
            return SongTime;
        }

        public bool Receive(int pitch, bool on, double now, bool playing)
        {
            Advance(now, playing);
            if (!on) { held.Remove(pitch); return false; }
            if (!held.Add(pitch) || !playing || CompletedGroups >= groups.Count) return false;
            Group group = groups[CompletedGroups];
            if (SongTime < group.Time - EarlyWindow || !group.Pitches.Contains(pitch)) return false;
            bool accepted = credited.Add(pitch);
            if (Waiting && credited.Count == group.Pitches.Count) CompleteGroup();
            return accepted;
        }

        public void ResetInput()
        {
            held.Clear();
            credited.Clear();
        }

        private void CompleteGroup() { CompletedGroups++; credited.Clear(); }
    }
}
