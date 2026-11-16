using Assets.Scripts.Songs;
using Newtonsoft.Json;
using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using UnityEngine;
using UnityEngine.Networking;

internal class ISongJson : ISongMetadata
{
    public ISongJson() { difficulty = SongDifficulty.Unknown; }
}

public class SongData : ISongMetadata
{
    public PartialSongData PartialSongData { get; private set; }
    public bool IsExternal { get { return PartialSongData.IsExternal; } }

    public SongData(string title, string[] artists, short releaseYear, uint duration,
        SongDifficulty difficulty, PartialSongData partialSongData)
    {
        this.title = title;
        this.releaseYear = releaseYear;
        this.duration = duration;
        this.difficulty = difficulty;
        this.artists = artists == null ? new[] { "n/a" } : (string[])artists.Clone();
        PartialSongData = new PartialSongData(partialSongData);
    }

    public SongData(ISongMetadata metadata, PartialSongData partialSongData)
        : this(metadata.title, metadata.artists, metadata.releaseYear, metadata.duration, metadata.difficulty, partialSongData) { }

    public override string ToString()
    {
        return $"{string.Join(", ", artists)} - {title} [{releaseYear}] ({duration}s, {difficulty})";
    }
}

public class SongLoader : MonoBehaviour
{
    internal static Action<LinkedList<SongData>> OnSongsLoaded;
    internal static LinkedList<SongData> CurrentSongs { get; private set; }
    internal static string ImportStatus { get; private set; }
    [SerializeField] private SongsFinder _songsFinder;
    private Coroutine loadingRoutine;
    private bool started;
    private bool completed;

    [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.SubsystemRegistration)]
    private static void ResetStatics()
    {
        CurrentSongs = null;
        OnSongsLoaded = null;
        ImportStatus = null;
    }

    // A late/duplicate Awake must not erase a catalog already received by the menu.
    private void Awake() { }
    private void Start() { started = true; BeginLoading(); }
    private void OnEnable() { if (started) BeginLoading(); }
    private void OnDisable()
    {
        if (loadingRoutine == null) return;
        StopCoroutine(loadingRoutine);
        loadingRoutine = null;
        if (!completed) ImportStatus = "Personal MIDI loading interrupted; restart the app to retry.";
    }

    private void BeginLoading()
    {
        if (completed || loadingRoutine != null) return;
        loadingRoutine = StartCoroutine(FetchSongs());
    }

    private IEnumerator FetchSongs()
    {
        var songs = new LinkedList<SongData>();
        var bundled = new LinkedList<PartialSongData>();
        var messages = new List<string>();
        string folder = Path.Combine(Application.persistentDataPath, "Music");
        ImportStatus = "Loading personal MIDI...";
        Debug.Log("[MIDI import] Catalog startup. Personal MIDI folder: " + folder);
        WriteReport(folder, "Personal MIDI folder: " + folder + "\nSong catalog loading started; waiting for bundled discovery.\n", messages);
        if (_songsFinder != null)
            yield return Guard(_songsFinder.FindJsons(found => bundled = found),
                e => messages.Add("Bundled discovery failed: " + Describe(e)));
        else
            Debug.LogWarning("Bundled song finder is missing; loading personal MIDI songs only.");

        foreach (PartialSongData song in bundled ?? new LinkedList<PartialSongData>())
        {
            string metadataJson = null;
            if (song.HasCorrespondingMetadata)
                yield return Guard(ReadBundledMetadata(song.MetadataPath, text => metadataJson = text),
                    e => messages.Add(song.JsonPath + ": metadata unavailable (" + Describe(e) + ")."));
            songs.AddLast(CreateSong(song, metadataJson, messages));
        }

        // Unity APIs remain on the main thread; file parsing runs off the render thread.
        Task<ExternalMidiScanResult> scan = Task.Run(() => ExternalMidiLibrary.Scan(folder));
        while (!scan.IsCompleted) yield return null;
        if (scan.IsFaulted || scan.IsCanceled)
        {
            messages.Add("Could not import personal MIDI songs: " + (scan.IsCanceled ? "Import canceled." : Describe(scan.Exception.GetBaseException())));
        }
        else
        {
            messages.AddRange(scan.Result.Errors);
            foreach (ExternalMidiSong imported in scan.Result.Songs)
            {
                var data = new PartialSongData(imported.FileName, imported.Json, imported.MetadataJson, imported.DurationSeconds);
                songs.AddLast(CreateSong(data, imported.MetadataJson, messages));
            }
        }

        int importedCount = songs.Count(song => song.IsExternal);
        ImportStatus = messages.Count == 0 ? null : "Personal MIDI: " + importedCount + " imported. " + messages[0];
        string report = "Personal MIDI folder: " + folder + "\n" +
            "Imported songs: " + importedCount + "\n" +
            "Close and reopen the app after adding, replacing or removing MIDI files.\n" +
            "Personal MIDI songs are available in single-player only.\n\n" +
            (messages.Count == 0 ? "No import errors.\n" : string.Join("\n", messages) + "\n");
        WriteReport(folder, report, messages);
        foreach (string message in messages) Debug.LogWarning("[MIDI import] " + message);
        if (messages.Count > 0) ImportStatus = "Personal MIDI: " + importedCount + " imported. " + messages[0];
        Debug.Log(report);
        completed = true;
        loadingRoutine = null;
        CurrentSongs = new LinkedList<SongData>(songs);
        OnSongsLoaded?.Invoke(new LinkedList<SongData>(songs));
    }

    // Unity otherwise abandons the entire parent coroutine when a nested finder throws.
    // Isolate bundled discovery so an APK/manifest problem cannot suppress personal files.
    private static IEnumerator Guard(IEnumerator routine, Action<Exception> failed)
    {
        var stack = new Stack<IEnumerator>();
        stack.Push(routine);
        try
        {
            while (stack.Count > 0)
            {
                bool next = false;
                object value = null;
                Exception error = null;
                try { next = stack.Peek().MoveNext(); if (next) value = stack.Peek().Current; }
                catch (Exception e) { error = e; }
                if (error != null) { failed(error); yield break; }
                if (!next) { (stack.Pop() as IDisposable)?.Dispose(); continue; }
                if (value is IEnumerator child) stack.Push(child);
                else yield return value;
            }
        }
        finally { while (stack.Count > 0) (stack.Pop() as IDisposable)?.Dispose(); }
    }

    private static string Describe(Exception error) { return error.GetType().Name + ": " + error.Message; }

    private static void WriteReport(string folder, string report, List<string> warnings)
    {
        try { Directory.CreateDirectory(folder); File.WriteAllText(Path.Combine(folder, "import-report.txt"), report); }
        catch (Exception e)
        {
            string warning = "Could not write Music/import-report.txt: " + Describe(e);
            if (!warnings.Contains(warning)) warnings.Add(warning);
            // A shell-created or inaccessible Music folder must still leave useful evidence.
            try { File.WriteAllText(Path.Combine(Application.persistentDataPath, "midi-import-report.txt"), report + "\n" + warning); }
            catch (Exception fallback) { Debug.LogWarning("Could not write fallback MIDI report: " + Describe(fallback)); }
        }
    }

    internal static SongData CreateSong(PartialSongData song, string metadataJson, List<string> warnings)
    {
        string fileName = song.IsExternal ? song.JsonPath.Substring("user-midi:".Length) : song.JsonPath;
        string fallbackTitle = Path.GetFileNameWithoutExtension(fileName);
        ISongJson metadata = null;
        if (!string.IsNullOrWhiteSpace(metadataJson))
        {
            try
            {
                metadata = JsonConvert.DeserializeObject<ISongJson>(metadataJson);
                if (metadata == null) throw new FormatException("Metadata must be an object.");
                if (!Enum.IsDefined(typeof(SongDifficulty), metadata.difficulty))
                    throw new FormatException("Unknown song difficulty.");
            }
            catch (Exception e)
            {
                warnings?.Add(fileName + ": metadata ignored (" + e.Message + ").");
                metadata = null;
            }
        }
        string title = string.IsNullOrWhiteSpace(metadata?.title) ? fallbackTitle : metadata.title;
        var artists = metadata?.artists?.Where(artist => !string.IsNullOrWhiteSpace(artist)).ToArray();
        if (artists == null || artists.Length == 0) artists = new[] { song.IsExternal ? "Personal MIDI" : "n/a" };
        uint duration = song.IsExternal ? (uint)Math.Ceiling(song.ImportedDurationSeconds) : metadata?.duration ?? 0;
        return new SongData(title, artists, metadata?.releaseYear ?? -1, duration,
            metadata?.difficulty ?? SongDifficulty.Unknown, song);
    }

    private IEnumerator ReadBundledMetadata(string path, Action<string> callback)
    {
        string fullPath = Path.Combine(Application.streamingAssetsPath, path);
#if UNITY_ANDROID && !UNITY_EDITOR
        using (UnityWebRequest www = UnityWebRequest.Get(fullPath))
        {
            yield return www.SendWebRequest();
            callback(www.result == UnityWebRequest.Result.Success ? www.downloadHandler.text : null);
        }
#else
        string text = null;
        try { if (File.Exists(fullPath)) text = File.ReadAllText(fullPath); }
        catch (Exception e) { Debug.LogWarning("Could not read song metadata: " + e.Message); }
        callback(text);
        yield return null;
#endif
    }
}
