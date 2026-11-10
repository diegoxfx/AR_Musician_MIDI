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
    [SerializeField] private SongsFinder _songsFinder;

    [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.SubsystemRegistration)]
    private static void ResetStatics()
    {
        CurrentSongs = null;
        OnSongsLoaded = null;
    }

    private void Awake() { CurrentSongs = null; }
    // Start follows all scene Awakes, so the selector subscribes before publication.
    private void Start() { StartCoroutine(FetchSongs()); }

    private IEnumerator FetchSongs()
    {
        var songs = new LinkedList<SongData>();
        var bundled = new LinkedList<PartialSongData>();
        if (_songsFinder != null)
            yield return _songsFinder.FindJsons(found => bundled = found);
        else
            Debug.LogWarning("Bundled song finder is missing; loading personal MIDI songs only.");

        foreach (PartialSongData song in bundled)
        {
            string metadataJson = null;
            if (song.HasCorrespondingMetadata)
                yield return ReadBundledMetadata(song.MetadataPath, text => metadataJson = text);
            songs.AddLast(CreateSong(song, metadataJson, null));
        }

        // Unity APIs remain on the main thread; file parsing runs off the render thread.
        string folder = Path.Combine(Application.persistentDataPath, "Music");
        Task<ExternalMidiScanResult> scan = Task.Run(() => ExternalMidiLibrary.Scan(folder));
        while (!scan.IsCompleted) yield return null;
        var messages = new List<string>();
        if (scan.IsFaulted || scan.IsCanceled)
        {
            messages.Add("Could not import personal MIDI songs: " + scan.Exception?.GetBaseException().Message);
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

        foreach (string message in messages) Debug.LogWarning("[MIDI import] " + message);
        string report = "Personal MIDI folder: " + folder + "\n" +
            "Imported songs: " + songs.Count(song => song.IsExternal) + "\n" +
            "Close and reopen the app after adding, replacing or removing MIDI files.\n" +
            "Personal MIDI songs are available in single-player only.\n\n" +
            (messages.Count == 0 ? "No import errors.\n" : string.Join("\n", messages) + "\n");
        try { File.WriteAllText(Path.Combine(folder, "import-report.txt"), report); }
        catch (Exception e) { Debug.LogWarning("Could not write MIDI import report: " + e.Message); }
        Debug.Log(report);
        CurrentSongs = new LinkedList<SongData>(songs);
        OnSongsLoaded?.Invoke(new LinkedList<SongData>(songs));
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
