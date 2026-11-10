using Melanchall.DryWetMidi.Core;
using Melanchall.DryWetMidi.Interaction;
using System.Collections.Generic;
using Newtonsoft.Json;
using System.IO;

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

            // Extract note events
            var notes = midiFile.GetNotes();

            List<NoteEvent> noteList = new List<NoteEvent>();

            foreach (var note in notes)
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
                    time = (float)startSec,
                    duration = (float)durSec
                });
            }

            // Convert to JSON
            string json = JsonConvert.SerializeObject(noteList, Formatting.Indented);

            // Save JSON
            File.WriteAllText(outputPath, json);
        }

        public static string GetNoteName(int midiNumber)
        {
            int octave = midiNumber / 12 - 1;
            string note = noteNames[midiNumber % 12];
            return note + octave;
        }
    }
}
