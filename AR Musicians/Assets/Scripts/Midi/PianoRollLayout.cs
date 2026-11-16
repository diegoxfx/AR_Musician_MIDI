using System;
using System.Collections.Generic;

namespace ARMusician.Midi
{
    /// <summary>Visible note lengths only. MIDI onset, gate and audio duration remain unchanged.</summary>
    public static class PianoRollLayout
    {
        public const float RepeatGapSeconds = 0.035f;

        public static void Prepare(List<NoteEvent> notes)
        {
            if (notes == null) throw new ArgumentNullException(nameof(notes));
            var pitches = new Dictionary<int, List<NoteEvent>>();
            foreach (var note in notes)
            {
                if (note == null) continue;
                note.visualDuration = Math.Max(0.015f, note.duration);
                List<NoteEvent> events;
                if (!pitches.TryGetValue(note.keyIndex, out events))
                    pitches[note.keyIndex] = events = new List<NoteEvent>();
                events.Add(note);
            }
            foreach (var events in pitches.Values)
            {
                events.Sort((a, b) => a.time.CompareTo(b.time));
                // Process equal-onset groups together: duplicate tracks are one visual strike,
                // not zero-duration repeats. Every true next onset gets its own gap.
                int first = 0;
                while (first < events.Count)
                {
                    int next = first + 1;
                    while (next < events.Count && events[next].time == events[first].time) next++;
                    if (next < events.Count)
                    {
                        float distance = events[next].time - events[first].time;
                        float gap = Math.Min(RepeatGapSeconds, distance * 0.25f);
                        float visible = Math.Max(0, distance - gap);
                        for (int index = first; index < next; index++)
                            events[index].visualDuration = Math.Min(events[index].visualDuration, visible);
                    }
                    first = next;
                }
            }
        }
    }
}
