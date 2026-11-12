using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using ARMusician.Midi;
using Assets.Scripts.Songs;
using Newtonsoft.Json;

public static class ExternalMidiLibraryTests
{
    private static int assertions;
    public static void Run()
    {
        string root = Path.Combine(Path.GetTempPath(), "AR-Musician-ImportTests-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            TempoAndTracks(Path.Combine(root, "timing"));
            TrackHands(Path.Combine(root, "hands"));
            FreshSnapshot(Path.Combine(root, "fresh"));
            IsolationAndMetadata(Path.Combine(root, "isolation"));
            Bounds(Path.Combine(root, "bounds"));
            Console.WriteLine("External MIDI import tests passed (" + assertions + " assertions; real shipped DryWetMidi parser).");
        }
        finally { Directory.Delete(root, true); }
    }

    private static void Check(bool value, string description)
    { assertions++; if (!value) throw new Exception(description); }
    private static void Near(double actual, double expected, string description)
    { Check(Math.Abs(actual - expected) < 0.00001, description + ": " + actual); }
    private static string Folder(string path) { Directory.CreateDirectory(path); return path; }
    private static NoteEvent[] Notes(ExternalMidiSong song) { return JsonConvert.DeserializeObject<NoteEvent[]>(song.Json); }
    private static byte[] NoteTrack(int pitch, int duration = 480, params string[] names)
    {
        var bytes = new List<byte>();
        foreach (string name in names)
        {
            var label = Encoding.ASCII.GetBytes(name);
            Event(bytes, 0, 0xff, 0x03, (byte)label.Length);
            bytes.AddRange(label);
        }
        Event(bytes, 0, 0x90, (byte)pitch, 100); Event(bytes, duration, 0x80, (byte)pitch, 0);
        return End(bytes);
    }
    private static void TrackHands(string folder)
    {
        Folder(folder);
        string midi = Path.Combine(folder, "hands.mid");
        WriteMidi(midi, NoteTrack(84, 480, "Piano Left-Hand"), NoteTrack(45, 480, "R.H. (Piano)"),
            NoteTrack(61, 480, "Left Hand", "Right Hand"), NoteTrack(47, 480, "Left"), NoteTrack(60));
        var scan = ExternalMidiLibrary.Scan(folder);
        Check(scan.Errors.Count == 0 && scan.Songs.Count == 1, "Named and unnamed tracks import in a single arrangement");
        var notes = Notes(scan.Songs[0]);
        Check(notes.Length == 5 && notes.All(n => n.time == 0), "Hand assignment preserves all same-onset notes");
        Check(notes.Single(n => n.midiNumber == 84).sourceHand == PianoHand.Left &&
            notes.Single(n => n.midiNumber == 45).sourceHand == PianoHand.Right, "Explicit track names survive import, even across the pitch split");
        Check(notes.Where(n => n.midiNumber == 61 || n.midiNumber == 47 || n.midiNumber == 60)
            .All(n => n.sourceHand == PianoHand.Unspecified), "Conflicting, ambiguous, and missing names leave hand assignment open");
        string json = Path.Combine(folder, "converted.json");
        MidiUtils.ConvertMidiToJson(midi, json);
        var converted = JsonConvert.DeserializeObject<NoteEvent[]>(File.ReadAllText(json));
        Check(converted.Select(n => (n.midiNumber, n.sourceHand)).SequenceEqual(notes.Select(n => (n.midiNumber, n.sourceHand))),
            "Editor JSON conversion preserves the same hand data as personal MIDI import");
    }
    private static void TempoAndTracks(string folder)
    {
        Folder(folder);
        var tempo = new List<byte>();
        Event(tempo, 0, 0xff, 0x51, 3, 7, 0xa1, 0x20); // 120 BPM
        Event(tempo, 480, 0xff, 0x51, 3, 0x0f, 0x42, 0x40); // 60 BPM at beat 1
        var part = new List<byte>();
        Event(part, 0, 0x90, 61, 100); Event(part, 0, 0x90, 64, 100);
        Event(part, 960, 0x80, 61, 0); Event(part, 0, 0x80, 64, 0);
        Event(part, 0, 0x90, 67, 100); Event(part, 480, 0x80, 67, 0);
        WriteMidi(Path.Combine(folder, "tempo.mid"), End(tempo), End(part), NoteTrack(61, 960));
        var scan = ExternalMidiLibrary.Scan(folder);
        Check(scan.Errors.Count == 0 && scan.Songs.Count == 1, "Tempo/multitrack import succeeds");
        var notes = Notes(scan.Songs[0]);
        Check(notes.Length == 4, "All tracks, chord notes and duplicate notes retained");
        Check(notes.Select(n => n.midiNumber).SequenceEqual(new[] { 61, 64, 61, 67 }), "Conversion preserves chronological order across tracks and track order at shared onsets");
        Check(notes.Count(n => n.midiNumber == 61 && n.key == "C#4") == 2, "Sharps preserve canonical MIDI pitch");
        foreach (var note in notes.Where(n => n.midiNumber == 61)) Near(note.duration, 1.5, "Note duration spans tempo change");
        Near(notes.Single(n => n.midiNumber == 67).time, 1.5, "Note start uses tempo map");
        Near(scan.Songs[0].DurationSeconds, 2.5, "Duration is latest note end across tracks");
    }
    private static void FreshSnapshot(string folder)
    {
        Folder(folder);
        string midi = Path.Combine(folder, "same.mid");
        WriteMidi(midi, NoteTrack(60));
        File.WriteAllText(Path.Combine(folder, "same.json"), "[stale invalid cache]");
        var first = ExternalMidiLibrary.Scan(folder);
        WriteMidi(midi, NoteTrack(70, 960));
        var second = ExternalMidiLibrary.Scan(folder);
        Check(Notes(first.Songs[0])[0].midiNumber == 60, "Existing snapshot unaffected by overwrite");
        Check(Notes(second.Songs[0])[0].midiNumber == 70, "Next startup reads same-name changed MIDI, ignoring stale JSON");
        Near(second.Songs[0].DurationSeconds, 1, "Changed MIDI duration is fresh");
        File.Delete(midi);
        Check(ExternalMidiLibrary.Scan(folder).Songs.Count == 0, "Removed MIDI absent despite adjacent JSON");
        WriteMidi(Path.Combine(folder, "b.MIDI"), NoteTrack(60));
        WriteMidi(Path.Combine(folder, "A.mid"), NoteTrack(61));
        WriteMidi(Path.Combine(folder, "A.midi"), NoteTrack(62));
        WriteMidi(Path.Combine(Folder(Path.Combine(folder, "nested")), "ignored.mid"), NoteTrack(63));
        var ordered = ExternalMidiLibrary.Scan(folder);
        Check(string.Join(",", ordered.Songs.Select(s => s.FileName)) == "A.mid,A.midi,b.MIDI", "Case-insensitive extensions, same-basename files and stable top-level order");
        Check(Notes(ordered.Songs[0])[0].midiNumber != Notes(ordered.Songs[1])[0].midiNumber, "Same basename retains independent contents");
        WriteMidi(Path.Combine(folder, "canción 日本.mid"), NoteTrack(66));
        var unicode = ExternalMidiLibrary.Scan(folder).Songs.Single(s => s.FileName == "canción 日本.mid");
        Check(Notes(unicode)[0].midiNumber == 66, "Non-ASCII filenames preserve identity and import contents");
    }
    private static void IsolationAndMetadata(string folder)
    {
        Folder(folder);
        WriteMidi(Path.Combine(folder, "good.mid"), NoteTrack(60));
        WriteMidi(Path.Combine(folder, "empty.mid"), End(new List<byte>()));
        File.WriteAllBytes(Path.Combine(folder, "bad.mid"), Encoding.ASCII.GetBytes("not MIDI"));
        WriteMidi(Path.Combine(folder, "truncated.mid"), NoteTrack(61));
        string truncated = Path.Combine(folder, "truncated.mid");
        var content = File.ReadAllBytes(truncated); File.WriteAllBytes(truncated, content.Take(content.Length - 1).ToArray());
        var invalidChunk = MidiBytes(NoteTrack(61));
        invalidChunk[18] = 0x7f; invalidChunk[19] = 0xff; invalidChunk[20] = 0xff; invalidChunk[21] = 0xff;
        File.WriteAllBytes(Path.Combine(folder, "huge-declared-track.mid"), invalidChunk);
        string sidecar = Path.Combine(folder, "good_metadata.json");
        File.WriteAllText(sidecar, "{malformed JSON");
        var scan = ExternalMidiLibrary.Scan(folder);
        Check(scan.Songs.Count == 1 && scan.Errors.Count == 4, "Malformed, empty, truncated and impossible-sized chunks isolated from valid song");
        Check(scan.Songs[0].MetadataJson == "{malformed JSON", "Optional metadata preserved for caller's safe JSON fallback");
        File.WriteAllText(sidecar, new string('x', ExternalMidiLibrary.MaxMetadataBytes + 1));
        scan = ExternalMidiLibrary.Scan(folder);
        Check(scan.Songs.Count == 1 && scan.Songs[0].MetadataJson == null && scan.Errors.Any(e => e.Contains("metadata ignored")), "Oversized metadata warns without losing MIDI");
        File.WriteAllBytes(sidecar, new byte[] { 0xff, 0xff });
        scan = ExternalMidiLibrary.Scan(folder);
        Check(scan.Songs.Count == 1 && scan.Songs[0].MetadataJson == null, "Invalid UTF8 metadata safely ignored");
        File.WriteAllText(sidecar, "{\"name\":\"Example\"}", new UTF8Encoding(true));
        scan = ExternalMidiLibrary.Scan(folder);
        Check(scan.Songs[0].MetadataJson == "{\"name\":\"Example\"}", "UTF8 byte order mark removed for JSON parsing");
        File.Delete(sidecar);
        scan = ExternalMidiLibrary.Scan(folder);
        Check(scan.Songs[0].MetadataJson == null, "Missing optional metadata supported");
        Check(File.ReadAllBytes(Path.Combine(folder, "good.mid")).SequenceEqual(MidiBytes(NoteTrack(60))), "Source MIDI never modified");
    }
    private static void Bounds(string folder)
    {
        Folder(folder);
        WriteMidi(Path.Combine(folder, "good.mid"), NoteTrack(60));
        File.WriteAllBytes(Path.Combine(folder, "huge.mid"), new byte[ExternalMidiLibrary.MaxFileBytes + 1]);
        WriteMidi(Path.Combine(folder, "long.mid"), NoteTrack(60, 90000000));
        var many = new List<byte>();
        for (int i = 0; i <= ExternalMidiLibrary.MaxNotesPerSong; i++)
        { Event(many, 0, 0x90, 60, 100); Event(many, 1, 0x80, 60, 0); }
        WriteMidi(Path.Combine(folder, "many.mid"), End(many));
        var scan = ExternalMidiLibrary.Scan(folder);
        Check(scan.Songs.Count == 1 && scan.Errors.Count == 3, "File bytes, notes and duration limits isolate oversized songs");
        string countFolder = Folder(Path.Combine(folder, "count"));
        for (int i = 0; i < ExternalMidiLibrary.MaxFiles + 1; i++) WriteMidi(Path.Combine(countFolder, i.ToString("D3") + ".mid"), NoteTrack(60));
        scan = ExternalMidiLibrary.Scan(countFolder);
        Check(scan.Songs.Count == ExternalMidiLibrary.MaxFiles && scan.Errors.Count == 1, "Catalog candidate cap enforced");
        Check(scan.Songs.Last().FileName == "255.mid", "Candidate cap applied after deterministic ordering");
        string notesFolder = Folder(Path.Combine(folder, "catalog-notes"));
        var bounded = new List<byte>();
        for (int i = 0; i < 90000; i++)
        { Event(bounded, 0, 0x90, 60, 100); Event(bounded, 1, 0x80, 60, 0); }
        var track = End(bounded);
        for (int i = 0; i < 3; i++) WriteMidi(Path.Combine(notesFolder, i + ".mid"), track);
        scan = ExternalMidiLibrary.Scan(notesFolder);
        Check(scan.Songs.Count == 2 && scan.Errors.Any(e => e.Contains("Catalog exceeds")), "Total retained catalog note budget enforced");
        string entriesFolder = Folder(Path.Combine(folder, "entries"));
        for (int i = 0; i <= ExternalMidiLibrary.MaxDirectoryEntries; i++) File.WriteAllText(Path.Combine(entriesFolder, i + ".txt"), "");
        scan = ExternalMidiLibrary.Scan(entriesFolder);
        Check(scan.Songs.Count == 0 && scan.Errors.Count == 1, "Directory enumeration bounded even when files are not MIDI");
        string missing = Path.Combine(folder, "new");
        Check(ExternalMidiLibrary.Scan(missing).Songs.Count == 0 && Directory.Exists(missing), "Fresh installation creates Music directory");
        string notFolder = Path.Combine(folder, "not-folder"); File.WriteAllText(notFolder, "x");
        Check(ExternalMidiLibrary.Scan(notFolder).Errors.Count == 1, "Folder access failure returned as an error");
    }
    private static byte[] End(List<byte> bytes) { Event(bytes, 0, 0xff, 0x2f, 0); return bytes.ToArray(); }
    private static void Event(List<byte> bytes, int delta, params byte[] data)
    {
        var variable = new List<byte> { (byte)(delta & 127) };
        while ((delta >>= 7) > 0) variable.Insert(0, (byte)((delta & 127) | 128));
        bytes.AddRange(variable); bytes.AddRange(data);
    }
    private static void WriteMidi(string path, params byte[][] tracks) { File.WriteAllBytes(path, MidiBytes(tracks)); }
    private static byte[] MidiBytes(params byte[][] tracks)
    {
        using (var stream = new MemoryStream())
        using (var writer = new BinaryWriter(stream))
        {
            writer.Write(Encoding.ASCII.GetBytes("MThd")); writer.Write(new byte[] { 0, 0, 0, 6, 0, (byte)(tracks.Length > 1 ? 1 : 0), 0, (byte)tracks.Length, 1, 0xe0 });
            foreach (var track in tracks)
            {
                writer.Write(Encoding.ASCII.GetBytes("MTrk"));
                writer.Write(new byte[] { (byte)(track.Length >> 24), (byte)(track.Length >> 16), (byte)(track.Length >> 8), (byte)track.Length });
                writer.Write(track);
            }
            return stream.ToArray();
        }
    }
}
