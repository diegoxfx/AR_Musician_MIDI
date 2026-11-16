// Desktop integration tests: real production loading/catalog/UI, real JSON and MIDI parsers.
// Stubs model only the Unity/project boundaries; this is not a Unity player test.
using System;
using System.Collections;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Threading;
using ARMusician.Midi;
using Assets.Scripts.Songs;
using UnityEngine;
using Debug = UnityEngine.Debug;

namespace UnityEngine
{
    public class GameObject
    {
        public T AddComponent<T>() where T : Component, new() { return new T { gameObject = this }; }
    }
    public class Component
    {
        public GameObject gameObject = new GameObject();
        public T GetComponent<T>() where T : Component { return null; }
    }
    public class Coroutine { public IEnumerator Routine; public bool Cancelled; }
    public class MonoBehaviour : Component
    {
        public readonly List<Coroutine> StartedCoroutines = new List<Coroutine>();
        public Coroutine StartCoroutine(IEnumerator routine)
        { var result = new Coroutine { Routine = routine }; StartedCoroutines.Add(result); routine.MoveNext(); return result; }
        public void StopCoroutine(Coroutine routine) { routine.Cancelled = true; (routine.Routine as IDisposable)?.Dispose(); }
    }
    public class AudioSource : Component { }
    public class HeaderAttribute : Attribute { public HeaderAttribute(string text) { } }
    public class SerializeField : Attribute { }
    public enum RuntimeInitializeLoadType { SubsystemRegistration }
    public class RuntimeInitializeOnLoadMethodAttribute : Attribute { public RuntimeInitializeOnLoadMethodAttribute(RuntimeInitializeLoadType type) { } }
    public class WaitForSeconds { public WaitForSeconds(float seconds) { } }
    public static class Time { public static float time; }
    public static class Mathf { public static float Max(float first, float second) { return Math.Max(first, second); } }
    public static class Application { public static string streamingAssetsPath, persistentDataPath; }
    public static class Debug
    {
        public static readonly List<string> Warnings = new List<string>();
        public static void Log(object value) { }
        public static void LogWarning(object value) { Warnings.Add(Convert.ToString(value)); }
        public static void LogError(object value) { Warnings.Add(Convert.ToString(value)); }
    }
    public static class JsonUtility { public static T FromJson<T>(string value) { return Newtonsoft.Json.JsonConvert.DeserializeObject<T>(value); } }
}
namespace UnityEngine.Networking
{
    public class UnityWebRequest : IDisposable
    {
        public enum Result { Success }
        public Result result; public string error; public DownloadHandler downloadHandler = new DownloadHandler();
        public static UnityWebRequest Get(string path) { throw new NotSupportedException("Desktop suite must not use Android web requests."); }
        public object SendWebRequest() { return null; }
        public void Dispose() { }
    }
    public class DownloadHandler { public string text; }
}
namespace TMPro { public class TMP_Text : Component { public string text; public bool richText = true; } }
public static class ProjectConfig
{
    public static readonly SettingsData Settings = new SettingsData();
    public class SettingsData { public bool playing, master, enableMultiplayer, useMultiplayer; public string instrument; }
}
public static class OVRManager { public static event Action InputFocusLost, InputFocusAcquired; }
public class MenuControllerGeneric
{
    public int PostSongChoiceCalls;
    public void postSongChoiceMenu() { PostSongChoiceCalls++; }
    public void ShowMultiplayerPauseMenu(bool master) { } public void ShowSingleplayerPauseMenu() { }
    public void ShowMultiplayerLobbyMenu() { } public void ShowSinglePlayerLobbyMenu() { } public void ShowSongMenu() { }
}
public class FeedbackStub { public bool IsCalibrated; public int FirstNote = 36, LastNote = 96; public void BeginSong(List<NoteEvent> notes, float speed) { } public void EndSong(bool finished) { } }
public class MidiReferenceAudio : MonoBehaviour
{
    public bool SoundEnabled;
    public void BeginSong(List<NoteEvent> notes, float speed) { }
    public void Sync(double time, bool playing) { }
    public void Stop() { }
}
public class PianoStrategy : IGameplayStrategy
{
    public FeedbackStub MidiFeedback;
    public PlaneController plane = new PlaneController();
    public float ApproachTime { get { return 1; } }
    public bool IsInstrumentReady() { return true; }
    public void PreprocessNote(NoteEvent note) { note.keyIndex = note.midiNumber; }
    public void SpawnNote(NoteEvent note, float speed) { }
    public event Action OnNoteFinished;
}
public class PlaneController { public float GetLocalKeyWidth(int pitch) { return pitch >= 36 && pitch <= 96 ? 1 : 0; } }
public class BongoStrategy : PianoStrategy { public int average_key; }

public static class SongLoadingIntegrationTests
{
    private static int assertions;
    private const string Notes60 = "[{\"key\":\"C4\",\"midiNumber\":60,\"time\":0,\"duration\":0.5}]";
    private const string Notes72 = "[{\"key\":\"C5\",\"midiNumber\":72,\"time\":1,\"duration\":0.25}]";
    private static void Check(bool value, string message) { assertions++; if (!value) throw new Exception(message); }
    private static object Field(object target, string name) { return target.GetType().GetField(name, BindingFlags.NonPublic | BindingFlags.Instance).GetValue(target); }
    private static void Set(object target, string name, object value) { target.GetType().GetField(name, BindingFlags.NonPublic | BindingFlags.Instance).SetValue(target, value); }
    private static object Call(object target, string name) { return target.GetType().GetMethod(name, BindingFlags.NonPublic | BindingFlags.Instance).Invoke(target, null); }
    private static void Reset()
    {
        typeof(SongLoader).GetMethod("ResetStatics", BindingFlags.NonPublic | BindingFlags.Static).Invoke(null, null);
        ProjectConfig.Settings.enableMultiplayer = ProjectConfig.Settings.useMultiplayer = ProjectConfig.Settings.playing = false;
        Debug.Warnings.Clear();
    }
    private static SongData External(string json = Notes60) { return SongLoader.CreateSong(new PartialSongData("personal.mid", json, null, 0.5), null, null); }
    private static SongData Bundled(string path = "bundle.json") { return SongLoader.CreateSong(new PartialSongData(path, null, false), null, null); }
    private static List<NoteEvent> Loaded(RhythmGameManager manager) { return (List<NoteEvent>)Field(manager, "loadedNotes"); }
    private static void Drain(IEnumerator routine)
    {
        var clock = Stopwatch.StartNew();
        while (routine.MoveNext())
        {
            var child = routine.Current as IEnumerator;
            if (child != null) Drain(child); else Thread.Sleep(1);
            if (clock.Elapsed.TotalSeconds > 10) throw new TimeoutException("Coroutine did not finish within 10 seconds.");
        }
    }
    public static void Run()
    {
        string root = Path.Combine(Path.GetTempPath(), "AR-Musician-LoadingTests-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        Application.streamingAssetsPath = Path.Combine(root, "bundled");
        Application.persistentDataPath = Path.Combine(root, "persistent");
        Directory.CreateDirectory(Application.streamingAssetsPath);
        Directory.CreateDirectory(Application.persistentDataPath);
        try
        {
            MetadataAndSnapshots(); SelectorLifecycle(); GameplayLoading(); StartupCatalog(); LoaderRecovery();
            Console.WriteLine("Song loading integration tests passed (" + assertions + " assertions; real source, Newtonsoft and DryWetMidi).");
        }
        finally { Reset(); Directory.Delete(root, true); }
    }
    private static void MetadataAndSnapshots()
    {
        Reset();
        var warnings = new List<string>();
        var source = new PartialSongData("snapshot.mid", Notes60, "{invalid}", 2.01);
        var copy = new PartialSongData(source);
        Check(copy.IsExternal && copy.JsonPath == source.JsonPath && copy.InlineJson == Notes60 && copy.InlineMetadataJson == "{invalid}" && copy.ImportedDurationSeconds == 2.01, "PartialSongData copy preserves every startup snapshot field");
        var song = SongLoader.CreateSong(copy, copy.InlineMetadataJson, warnings);
        Check(song.title == "snapshot" && song.artists.SequenceEqual(new[] { "Personal MIDI" }) && song.duration == 3 && song.difficulty == SongDifficulty.Unknown, "Malformed external metadata falls back while actual rounded duration survives");
        Check(warnings.Count == 1, "Malformed metadata produces one diagnostic");
        var data = new PartialSongData("empty-title.midi", Notes60, null, 0.25);
        song = SongLoader.CreateSong(data, "{\"title\":\"  \",\"artists\":null,\"duration\":9999}", warnings);
        Check(song.title == "empty-title" && song.artists[0] == "Personal MIDI" && song.duration == 1, "Blank title/null artists fallback and metadata cannot override imported duration");
        song = SongLoader.CreateSong(data, "{\"artists\":[null,\" \",\"Artist\"],\"title\":\"<b>Title</b>\",\"difficulty\":\"Hard\"}", warnings);
        Check(song.artists.SequenceEqual(new[] { "Artist" }) && song.difficulty == SongDifficulty.Hard, "Metadata removes empty artist entries and accepts named difficulty");
        foreach (string invalid in new[] { "null", "[]", "{\"difficulty\":999}", "{\"difficulty\":\"Impossible\"}" })
        {
            int before = warnings.Count;
            song = SongLoader.CreateSong(data, invalid, warnings);
            Check(song.title == "empty-title" && warnings.Count == before + 1, "Invalid metadata is isolated: " + invalid);
        }
        song = SongLoader.CreateSong(new PartialSongData("Music/bundled.json", null, false), "{\"title\":\"Bundle\",\"artists\":[],\"duration\":42}", warnings);
        Check(song.duration == 42 && song.artists[0] == "n/a" && !song.IsExternal, "Bundled metadata duration and artist fallback remain supported");
        var artists = new[] { "Original" };
        song = new SongData("copy", artists, -1, 1, SongDifficulty.Unknown, source);
        artists[0] = "Changed";
        Check(song.artists[0] == "Original" && !ReferenceEquals(song.PartialSongData, source) && song.PartialSongData.IsExternal, "SongData defensively copies artists and external PartialSongData");
    }
    private static SongsUIHandlerGeneric Selector(out TMPro.TMP_Text title)
    {
        var selector = new SongsUIHandlerGeneric(); title = new TMPro.TMP_Text();
        Set(selector, "songTitleTMP", title); return selector;
    }
    private static void SelectorLifecycle()
    {
        Reset(); TMPro.TMP_Text title;
        var selector = Selector(out title);
        var manager = new RhythmGameManager { menucontroller = new MenuControllerGeneric() };
        selector.manager = manager;
        int selectionEvents = 0;
        Action onSelected = () => selectionEvents++;
        SongsUIHandlerGeneric.OnSelectSong += onSelected;
        Call(selector, "OnEnable");
        Check(title.text == "Loading songs..." && !title.richText, "Unpublished catalog shows literal loading label");
        selector.OnNextSongSelected(); selector.OnPreviousSongSelected(); selector.OnSongSelected();
        Check(selector.SelectedSong == null && selector.getSongData(-1) == null && selector.getSongData(0) == null, "Loading selector buttons and invalid indices are safe");
        Check(manager.menucontroller.PostSongChoiceCalls == 0 && selector.StartedCoroutines.Count == 0 && selectionEvents == 0, "Selecting while loading neither navigates nor starts a song/event");
        SongLoader.OnSongsLoaded(new LinkedList<SongData>());
        selector.OnNextSongSelected(); selector.OnPreviousSongSelected(); selector.OnSongSelected();
        Check(title.text == "No songs available" && selector.SelectedSong == null, "Empty published catalog stays usable");
        Check(manager.menucontroller.PostSongChoiceCalls == 0 && selector.StartedCoroutines.Count == 0 && selectionEvents == 0, "Selecting an empty catalog does not leave the song menu");
        var external = External(); var bundled = Bundled();
        var catalog = new LinkedList<SongData>(new[] { external, bundled });
        SongLoader.OnSongsLoaded(catalog);
        Check(selector.SelectedSong == external, "Single player includes personal MIDI");
        selector.OnSongSelected();
        Check(manager.menucontroller.PostSongChoiceCalls == 1 && selector.StartedCoroutines.Count == 1 && selectionEvents == 1, "Valid single player selection starts loading and navigates exactly once");
        manager.SetApplicationFocused(false); selector.OnSongSelected();
        Check(manager.menucontroller.PostSongChoiceCalls == 1 && selector.StartedCoroutines.Count == 1 && selectionEvents == 1, "Focus-blocked selection neither navigates nor starts another song/event");
        manager.SetApplicationFocused(true);
        selector.manager = null; selector.OnSongSelected(); selector.manager = manager;
        Check(manager.menucontroller.PostSongChoiceCalls == 1 && selectionEvents == 1, "Missing manager selection is rejected without navigation/event");
        SongsUIHandlerGeneric.OnSelectSong -= onSelected;
        ProjectConfig.Settings.enableMultiplayer = true; ProjectConfig.Settings.useMultiplayer = true;
        Check(selector.getSongData(0) == bundled && selector.getSongData(1) == null && selector.getSongData(int.MaxValue) == null, "RPC index lookup refreshes mode and excludes personal songs before Update");
        selector.SelectedSong = external;
        Check(selector.SelectedSong == bundled, "Direct stale selection cannot restore a personal song in multiplayer");
        selector.OnPreviousSongSelected(); selector.OnNextSongSelected();
        Check(selector.SelectedSong == bundled, "Navigation wraps only within shared catalog");
        ProjectConfig.Settings.useMultiplayer = false;
        Check(selector.getSongData(0) == external && selector.getSongData(1) == bundled, "Single player mode restores full catalog");
        ProjectConfig.Settings.enableMultiplayer = false; ProjectConfig.Settings.useMultiplayer = true;
        Check(selector.getSongData(0) == external, "Use-multiplayer preference alone does not hide songs when multiplayer is disabled");
        Call(selector, "OnDisable");
        Check(SongLoader.OnSongsLoaded == null, "Disable removes catalog subscription");
        SongLoader.OnSongsLoaded?.Invoke(new LinkedList<SongData>());
        Check(selector.SelectedSong == external, "Disabled selector does not receive new publications");
        Call(selector, "OnEnable"); Call(selector, "OnDisable");
        Check(SongLoader.OnSongsLoaded == null, "Repeated enable/disable does not leak subscriptions");
    }
    private static void GameplayLoading()
    {
        Reset();
        var manager = new RhythmGameManager { menucontroller = new MenuControllerGeneric() };
        manager.SetStrategy(new PianoStrategy());
        File.WriteAllText(Path.Combine(Application.streamingAssetsPath, "bundle.json"), Notes72);
        Drain(manager.updateSongRoutine(External()));
        Check(Loaded(manager).Count == 1 && Loaded(manager)[0].midiNumber == 60, "External snapshot loads directly without an on-disk JSON file");
        Drain(manager.updateSongRoutine(Bundled()));
        Check(Loaded(manager)[0].midiNumber == 72, "Bundled JSON still loads from StreamingAssets");
        var older = manager.updateSongRoutine(External(Notes60));
        Check(older.MoveNext(), "First selection pauses before committing parsed notes");
        int earlierVersion = manager.SongLoadVersion;
        Check(manager.IsSongLoading && manager.LoadedSong == null && !manager.HasLoadedSong,
            "Beginning a load exposes pending state and invalidates previous identity/readiness");
        var newestSong = External(Notes72);
        Drain(manager.updateSongRoutine(newestSong));
        int completedVersion = manager.SongLoadVersion;
        Check(completedVersion > earlierVersion && !manager.IsSongLoading && manager.LoadedSong == newestSong,
            "Completed current load exposes identity and a new version for cached UI readiness");
        Drain(older);
        Check(manager.SongLoadVersion == completedVersion && manager.LoadedSong == newestSong && !manager.IsSongLoading,
            "Stale load cannot change current loading state, identity or readiness version");
        Check(Loaded(manager).Count == 1 && Loaded(manager)[0].midiNumber == 72, "Stale load cannot overwrite later selection");
        foreach (string invalid in new[] { "{broken", "null", "[null]", "[{\"time\":-1,\"duration\":1}]", "[{\"time\":0,\"duration\":-1}]", "[{\"time\":\"NaN\",\"duration\":1}]" })
        {
            Drain(manager.updateSongRoutine(External(invalid)));
            Check(Loaded(manager).Count == 0, "Invalid JSON/note data cannot leave a previously loaded song playable: " + invalid);
            Check(!manager.IsSongLoading && manager.LoadedSong == null,
                "Invalid note data completes with no loaded song identity");
        }
        Drain(manager.updateSongRoutine(Bundled("missing.json")));
        Check(Loaded(manager).Count == 0, "Missing bundled file is handled safely");
        Drain(manager.updateSongRoutine(External()));
        ProjectConfig.Settings.enableMultiplayer = ProjectConfig.Settings.useMultiplayer = true;
        Check(manager.BeginCountdown(false) < 0, "Mode flip blocks countdown of an already loaded personal song");
        manager.Play(); Check(!manager.playing, "Mode flip blocks direct playback of loaded personal song");
        ProjectConfig.Settings.useMultiplayer = false;
        int countdown = manager.BeginCountdown(false);
        Check(countdown >= 0, "Single player can begin countdown for personal MIDI");
        ProjectConfig.Settings.useMultiplayer = true;
        Check(!manager.CompleteCountdown(countdown, false) && !manager.playing, "Mode flip during countdown blocks completion into personal playback");
        ProjectConfig.Settings.useMultiplayer = false; manager.Play();
        Check(manager.playing, "Personal MIDI remains playable in single player");
        manager.Pause(); ProjectConfig.Settings.useMultiplayer = true;
        manager.Resume();
        Check(manager.State == RhythmGameManager.PlaybackState.Paused && manager.BeginCountdown(true) < 0, "Mode flip prevents both direct and countdown resume of personal MIDI");
        manager.Quit();
        Drain(manager.updateSongRoutine(Bundled()));
        Check(Loaded(manager)[0].midiNumber == 72, "Multiplayer can still load bundled songs");
        Drain(manager.updateSongRoutine(External()));
        Check(Loaded(manager)[0].midiNumber == 72, "Multiplayer rejects external load without replacing shared song");
        ProjectConfig.Settings.useMultiplayer = false;
        var pending = manager.updateSongRoutine(External()); pending.MoveNext();
        ProjectConfig.Settings.useMultiplayer = true; Drain(pending);
        Check(Loaded(manager).Count == 0 && manager.BeginCountdown(false) < 0, "Mode flip during external load rejects completion and cannot start personal playback");
    }
    private static void StartupCatalog()
    {
        Reset();
        string music = Path.Combine(Application.persistentDataPath, "Music"); Directory.CreateDirectory(music);
        // Format-0 MIDI with a left-hand track label and middle C held 480 ticks at 120 BPM.
        File.WriteAllBytes(Path.Combine(music, "personal.mid"), new byte[] {
            0x4d,0x54,0x68,0x64,0,0,0,6,0,0,0,1,1,0xe0,
            0x4d,0x54,0x72,0x6b,0,0,0,26,0,0xff,3,9,76,101,102,116,32,72,97,110,100,
            0,0x90,60,100,0x83,0x60,0x80,60,0,0,0xff,0x2f,0 });
        File.WriteAllText(Path.Combine(Application.streamingAssetsPath, "bundle_metadata.json"), "{\"title\":\"Bundled Title\",\"artists\":[\"Bundled Artist\"],\"duration\":9}");
        File.WriteAllText(Path.Combine(Application.streamingAssetsPath, "RuntimeActionBindings.json"), "{\"bindings\":[]}");
        var loader = new SongLoader(); Set(loader, "_songsFinder", new SongsFinder()); Call(loader, "Awake");
        int publications = 0; LinkedList<SongData> published = null;
        SongLoader.OnSongsLoaded += songs => { publications++; published = songs; };
        Check(SongLoader.CurrentSongs == null, "Startup catalog starts unpublished");
        Call(loader, "Start");
        Check(loader.StartedCoroutines.Count == 1 && publications == 0, "Start begins one asynchronous catalog operation");
        // StartCoroutine executes through its first yield, so pump that nested discovery too.
        IEnumerator fetch = loader.StartedCoroutines[0].Routine;
        if (fetch.Current is IEnumerator) Drain((IEnumerator)fetch.Current);
        Drain(fetch);
        Check(publications == 1 && SongLoader.CurrentSongs != null && published.Count == 2, "Startup publishes bundled plus personal catalog once after import completes");
        Check(published.All(song => !song.PartialSongData.JsonPath.EndsWith("RuntimeActionBindings.json", StringComparison.OrdinalIgnoreCase)),
            "Unity OpenXR configuration is excluded from the real startup song catalog");
        Check(published.First.Value.title == "Bundled Title" && !published.First.Value.IsExternal && published.Last.Value.IsExternal, "Bundled ordering/metadata remain before imported songs");
        var snapshot = published.Last.Value;
        Check(snapshot.duration == 1 && snapshot.PartialSongData.InlineJson.Contains("60"), "Real MIDI startup scan supplies note snapshot and actual duration");
        Check(File.ReadAllText(Path.Combine(music, "import-report.txt")).Contains("Imported songs: 1"), "Startup writes the import report");
        TMPro.TMP_Text title; var late = Selector(out title); Call(late, "OnEnable");
        Check(late.SelectedSong.title == "Bundled Title", "Late selector hydrates from already published cached catalog");
        Call(late, "OnDisable");
        File.Delete(Path.Combine(music, "personal.mid"));
        var manager = new RhythmGameManager(); Drain(manager.updateSongRoutine(snapshot));
        Check(Loaded(manager).Count == 1 && Loaded(manager)[0].midiNumber == 60, "Deleting source after startup does not invalidate playback snapshot");
        Check(Loaded(manager)[0].sourceHand == PianoHand.Left, "External track hand survives catalog snapshot and gameplay JSON loading");
        published.Clear();
        Check(SongLoader.CurrentSongs.Count == 2, "Subscriber collection mutation does not clear cached catalog");
        Call(new SongLoader(), "Awake");
        Check(SongLoader.CurrentSongs.Count == 2, "Late loader Awake cannot erase an already published catalog");
    }

    private class BrokenFinder : SongsFinder
    {
        public override IEnumerator FindJsons(Action<LinkedList<PartialSongData>> callback)
        { yield return FailLater(); }
        private IEnumerator FailLater() { yield return null; throw new IOException("broken APK manifest"); }
    }

    private static void LoaderRecovery()
    {
        Reset();
        string music = Path.Combine(Application.persistentDataPath, "Music");
        File.WriteAllBytes(Path.Combine(music, "recovery.mid"), new byte[] {
            0x4d,0x54,0x68,0x64,0,0,0,6,0,0,0,1,1,0xe0,
            0x4d,0x54,0x72,0x6b,0,0,0,13,0,0x90,60,100,0x83,0x60,0x80,60,0,0,0xff,0x2f,0 });
        var loader = new SongLoader(); Set(loader, "_songsFinder", new BrokenFinder());
        Call(loader, "Start");
        Check(File.ReadAllText(Path.Combine(music, "import-report.txt")).Contains("loading started"), "A diagnostic exists before asynchronous discovery completes");
        Coroutine interrupted = loader.StartedCoroutines[0];
        Call(loader, "OnDisable");
        Check(interrupted.Cancelled, "Disabling loader explicitly discards its incomplete coroutine");
        Call(loader, "OnEnable");
        Check(loader.StartedCoroutines.Count == 2, "Re-enabling restarts an interrupted import rather than staying stuck forever");
        IEnumerator fetch = loader.StartedCoroutines[1].Routine;
        if (fetch.Current is IEnumerator) Drain((IEnumerator)fetch.Current);
        Drain(fetch);
        Check(SongLoader.CurrentSongs.Count == 1 && SongLoader.CurrentSongs.First.Value.IsExternal, "A nested bundled discovery exception cannot suppress valid personal MIDI");
        Check(SongLoader.ImportStatus.Contains("broken APK manifest"), "Import failure retains a useful menu diagnostic");
        string report = File.ReadAllText(Path.Combine(music, "import-report.txt"));
        Check(report.Contains("Imported songs: 1") && report.Contains("IOException") && report.Contains("broken APK manifest"), "Final report identifies source/type and preserves personal import success");
        TMPro.TMP_Text title; var selector = Selector(out title); var artist = new TMPro.TMP_Text(); Set(selector, "songArtistTMP", artist);
        Call(selector, "OnEnable");
        Check(artist.text.Contains("broken APK manifest"), "Late menu shows failure even while a valid song is available");
        Call(selector, "OnDisable"); Call(loader, "OnEnable");
        Check(loader.StartedCoroutines.Count == 2, "Completed import is not duplicated by menu/lifecycle re-enables");
    }
}
