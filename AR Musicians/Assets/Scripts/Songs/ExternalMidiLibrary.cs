using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.IO;
using System.Text;
using Melanchall.DryWetMidi.Core;
using Melanchall.DryWetMidi.Interaction;
using Newtonsoft.Json;

namespace Assets.Scripts.Songs
{
    // One startup snapshot: playback never depends on a cache or a subsequently changed file.
    public sealed class ExternalMidiSong
    {
        public string FileName { get; private set; }
        public string Json { get; private set; }
        public double DurationSeconds { get; private set; }
        public string MetadataJson { get; private set; }

        internal ExternalMidiSong(string fileName, string json, double duration, string metadata)
        { FileName = fileName; Json = json; DurationSeconds = duration; MetadataJson = metadata; }
    }

    public sealed class ExternalMidiScanResult
    {
        public string Folder { get; private set; }
        public ReadOnlyCollection<ExternalMidiSong> Songs { get; private set; }
        public ReadOnlyCollection<string> Errors { get; private set; }

        internal ExternalMidiScanResult(string folder, List<ExternalMidiSong> songs, List<string> errors)
        { Folder = folder; Songs = songs.AsReadOnly(); Errors = errors.AsReadOnly(); }
    }

    public static class ExternalMidiLibrary
    {
        // Bounds cover parser allocation, directory work, retained JSON and conversion work.
        public const int MaxFileBytes = 1024 * 1024;
        public const int MaxMetadataBytes = 64 * 1024;
        public const int MaxDirectoryEntries = 4096;
        public const int MaxFiles = 256;
        public const int MaxNotesPerSong = 100000;
        public const int MaxCatalogNotes = 250000;
        public const long MaxCatalogBytes = 32L * 1024 * 1024;
        public const double MaxDurationSeconds = 24 * 60 * 60;

        public static ExternalMidiScanResult Scan(string folder)
        {
            var songs = new List<ExternalMidiSong>();
            var errors = new List<string>();
            var paths = new List<string>();
            try
            {
                folder = Path.GetFullPath(folder);
                Directory.CreateDirectory(folder);
                int entries = 0;
                foreach (string path in Directory.EnumerateFileSystemEntries(folder))
                {
                    if (++entries > MaxDirectoryEntries)
                        throw new InvalidDataException("Folder exceeds the " + MaxDirectoryEntries + " entry limit.");
                    string extension = Path.GetExtension(path);
                    if ((string.Equals(extension, ".mid", StringComparison.OrdinalIgnoreCase) ||
                         string.Equals(extension, ".midi", StringComparison.OrdinalIgnoreCase)) && File.Exists(path))
                        paths.Add(path);
                }
            }
            catch (Exception ex) when (IsFileError(ex))
            {
                errors.Add("Music folder: " + ex.Message);
                return new ExternalMidiScanResult(folder, songs, errors);
            }

            paths.Sort((a, b) =>
            {
                int result = StringComparer.OrdinalIgnoreCase.Compare(Path.GetFileName(a), Path.GetFileName(b));
                return result != 0 ? result : StringComparer.Ordinal.Compare(Path.GetFileName(a), Path.GetFileName(b));
            });
            if (paths.Count > MaxFiles)
                errors.Add("Only the first " + MaxFiles + " MIDI files in filename order are scanned.");

            int totalNotes = 0;
            long totalBytes = 0;
            for (int index = 0; index < paths.Count && index < MaxFiles; index++)
            {
                string path = paths[index];
                string fileName = Path.GetFileName(path);
                try
                {
                    byte[] bytes = ReadBounded(path, MaxFileBytes);
                    totalBytes += bytes.Length; // Include rejected files in the work budget.
                    if (totalBytes > MaxCatalogBytes)
                    {
                        errors.Add("Catalog exceeds the " + MaxCatalogBytes + " byte scan limit; remaining files skipped.");
                        break;
                    }
                    MidiFile midi;
                    using (var stream = new MemoryStream(bytes, false))
                        midi = MidiFile.Read(stream, new ReadingSettings
                        {
                            NotEnoughBytesPolicy = NotEnoughBytesPolicy.Abort,
                            InvalidChunkSizePolicy = InvalidChunkSizePolicy.Abort,
                            MissedEndOfTrackPolicy = MissedEndOfTrackPolicy.Abort,
                            UnexpectedTrackChunksCountPolicy = UnexpectedTrackChunksCountPolicy.Abort,
                            StopReadingOnExpectedTrackChunksCountReached = false,
                            ReaderSettings = new ReaderSettings { BytesPacketMaxLength = MaxFileBytes }
                        });

                    var tempoMap = midi.GetTempoMap();
                    var notes = new List<NoteEvent>();
                    double duration = 0;
                    foreach (var note in midi.GetNotes())
                    {
                        if (notes.Count >= MaxNotesPerSong)
                            throw new InvalidDataException("Song exceeds the " + MaxNotesPerSong + " note limit.");
                        double start = TimeConverter.ConvertTo<MetricTimeSpan>(note.Time, tempoMap).TotalSeconds;
                        double length = LengthConverter.ConvertTo<MetricTimeSpan>(note.Length, note.Time, tempoMap).TotalSeconds;
                        double end = start + length;
                        if (double.IsNaN(end) || double.IsInfinity(end) || start < 0 || length < 0 || end > MaxDurationSeconds)
                            throw new InvalidDataException("Note timing exceeds the 24-hour song limit.");
                        duration = Math.Max(duration, end);
                        notes.Add(new NoteEvent { key = MidiUtils.GetNoteName(note.NoteNumber), midiNumber = note.NoteNumber,
                            time = (float)start, duration = (float)length });
                    }
                    if (notes.Count == 0)
                        throw new InvalidDataException("MIDI contains no complete playable notes.");
                    totalNotes += notes.Count;
                    if (totalNotes > MaxCatalogNotes)
                    {
                        errors.Add("Catalog exceeds the " + MaxCatalogNotes + " note limit; remaining files skipped.");
                        break;
                    }
                    string metadata = null;
                    string metadataPath = Path.Combine(folder, Path.GetFileNameWithoutExtension(path) + "_metadata.json");
                    if (File.Exists(metadataPath))
                    {
                        try
                        {
                            metadata = new UTF8Encoding(false, true).GetString(ReadBounded(metadataPath, MaxMetadataBytes));
                            if (metadata.Length > 0 && metadata[0] == '\uFEFF') metadata = metadata.Substring(1);
                        }
                        catch (Exception ex) when (IsFileError(ex))
                        { errors.Add(fileName + " metadata ignored: " + ex.Message); }
                    }
                    songs.Add(new ExternalMidiSong(fileName, SerializeNotes(notes), duration, metadata));
                }
                // Untrusted files stay isolated even if the parser reports an unexpected format exception.
                catch (Exception ex) when (!(ex is OutOfMemoryException))
                { errors.Add(fileName + ": " + ex.GetType().Name + ": " + ex.Message); }
            }
            return new ExternalMidiScanResult(folder, songs, errors);
        }

        // Explicit field writes avoid reflection-generated collection serializers on IL2CPP.
        private static string SerializeNotes(List<NoteEvent> notes)
        {
            var builder = new StringBuilder();
            using (var output = new StringWriter(builder, System.Globalization.CultureInfo.InvariantCulture))
            using (var writer = new JsonTextWriter(output))
            {
                writer.WriteStartArray();
                foreach (NoteEvent note in notes)
                {
                    writer.WriteStartObject();
                    writer.WritePropertyName("key"); writer.WriteValue(note.key);
                    writer.WritePropertyName("midiNumber"); writer.WriteValue(note.midiNumber);
                    writer.WritePropertyName("time"); writer.WriteValue(note.time);
                    writer.WritePropertyName("duration"); writer.WriteValue(note.duration);
                    writer.WritePropertyName("keyIndex"); writer.WriteValue(note.keyIndex);
                    writer.WriteEndObject();
                }
                writer.WriteEndArray();
            }
            return builder.ToString();
        }

        private static byte[] ReadBounded(string path, int limit)
        {
            // Check and read through the same handle, then reject growth/truncation. Never ReadAllBytes after a separate size check.
            using (var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read))
            {
                long length = stream.Length;
                if (length > limit) throw new InvalidDataException("File exceeds the " + limit + " byte limit.");
                var bytes = new byte[(int)length];
                int offset = 0;
                while (offset < bytes.Length)
                {
                    int read = stream.Read(bytes, offset, bytes.Length - offset);
                    if (read == 0) throw new InvalidDataException("File changed or was truncated while reading.");
                    offset += read;
                }
                if (stream.ReadByte() != -1) throw new InvalidDataException("File grew while reading; retry after copying finishes.");
                return bytes;
            }
        }

        private static bool IsFileError(Exception ex)
        {
            return ex is IOException || ex is InvalidDataException || ex is UnauthorizedAccessException || ex is ArgumentException ||
                   ex is NotSupportedException || ex is System.Security.SecurityException;
        }
    }
}
