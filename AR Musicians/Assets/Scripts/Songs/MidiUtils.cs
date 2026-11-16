using Melanchall.DryWetMidi.Core;
using Melanchall.DryWetMidi.Interaction;
using System.Collections.Generic;
using System.Linq;
using Newtonsoft.Json;
using System.IO;
using ARMusician.Midi;

namespace Assets.Scripts.Songs
{
    public static class MidiUtils
    {
        private static readonly string[] noteNames =
            { "C", "C#", "D", "D#", "E", "F", "F#", "G", "G#", "A", "A#", "B" };

        public static void ConvertMidiToJson(string midiPath, string outputPath)
        {
            var midiFile = MidiFile.Read(midiPath);

            // Get tempo map (for converting ticks to seconds)
            var tempoMap = midiFile.GetTempoMap();

            List<NoteEvent> noteList = new List<NoteEvent>();

            foreach (var (note, hand) in ReadNotesWithHands(midiFile))
            {
                // Convert MIDI pitch to note name (like "C4")
                string noteName = GetNoteName(note.NoteNumber);

                // Convert time and duration to seconds
                double startSec = TimeConverter.ConvertTo<MetricTimeSpan>(note.Time, tempoMap).TotalSeconds;
                double durSec = LengthConverter.ConvertTo<MetricTimeSpan>(note.Length, note.Time, tempoMap).TotalSeconds;

                noteList.Add(new NoteEvent
                {
                    key = noteName,
                    midiNumber = note.NoteNumber,
                    sourceHand = hand,
                    time = (float)startSec,
                    duration = (float)durSec
                });
            }

            // Convert to JSON
            string json = JsonConvert.SerializeObject(noteList, Formatting.Indented);

            // Save JSON
            File.WriteAllText(outputPath, json);
        }

        internal static IEnumerable<(Note Note, PianoHand Hand)> ReadNotesWithHands(MidiFile midi)
        {
            // Match the chronological order of MidiFile.GetNotes while preserving the originating track.
            return midi.GetTrackChunks().SelectMany(track =>
            {
                var hand = ReadTrackHand(track);
                return track.GetNotes().Select(note => (Note: note, Hand: hand));
            }).OrderBy(item => item.Note.Time);
        }

        private static PianoHand ReadTrackHand(TrackChunk track)
        {
            PianoHand hand = PianoHand.Unspecified;
            foreach (var midiEvent in track.Events)
            {
                if (!(midiEvent is SequenceTrackNameEvent name)) continue;
                var candidate = PianoHandPartition.ParseTrackName(name.Text);
                if (candidate == PianoHand.Unspecified) continue;
                if (hand != PianoHand.Unspecified && hand != candidate) return PianoHand.Unspecified;
                hand = candidate;
            }
            return hand;
        }

        public static string GetNoteName(int midiNumber)
        {
            int octave = midiNumber / 12 - 1;
            string note = noteNames[midiNumber % 12];
            return note + octave;
        }
    }
}
