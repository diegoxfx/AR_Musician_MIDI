using System.Collections;
using System.Collections.Generic;
using UnityEngine;
using System.IO;
using Newtonsoft.Json;
using UnityEngine.Networking;
using System;
using ARMusician.Midi;

public class RhythmGameManager : MonoBehaviour
{
    [Header("References")]
    public MenuControllerGeneric menucontroller;
    public AudioSource audioSource;

    // This handles the specific logic for Piano OR Bongos
    private IGameplayStrategy currentStrategy;

    [Header("General Settings")]
    public float gameSpeed = 1f;

    private List<NoteEvent> notes = new List<NoteEvent>();
    private List<NoteEvent> loadedNotes = new List<NoteEvent>();
    private bool loadedSongIsExternal;
    private bool LoadedSongAllowed { get { return !loadedSongIsExternal ||
        !ProjectConfig.Settings.enableMultiplayer || !ProjectConfig.Settings.useMultiplayer; } }
    private int loadGeneration;
    private int playbackGeneration;
    private bool applicationPaused;
    private bool applicationFocused = true;
    private bool headsetFocused = true;
    public enum PlaybackState { Idle, Starting, Playing, Paused, Resuming }
    public PlaybackState State { get; private set; }
    public bool InputAllowed { get { return !applicationPaused && applicationFocused && headsetFocused; } }
    public bool CanSelectInstrument { get { return State == PlaybackState.Idle && InputAllowed; } }
    public bool IsPiano { get { return currentStrategy is PianoStrategy; } }
    public bool HasSession { get { return State == PlaybackState.Playing || State == PlaybackState.Paused || State == PlaybackState.Resuming; } }
    private float lastNoteTime;
    public double SongTime { get { return HasSession && learningPractice != null
        ? learningPractice.Advance(Time.time, playing)
        : (HasSession && !playing ? pauseBegin : Time.time) - songStartTime - totalPauseTime; } }
    private LearningPractice learningPractice;
    private MidiReferenceAudio referenceAudio;
    public bool LearningMode { get; private set; }
    public bool ReferenceSoundEnabled { get; private set; } = true;
    public bool WaitingForLearningNotes
    {
        get
        {
            if (learningPractice == null) return false;
            learningPractice.Advance(Time.time, playing);
            return learningPractice.Waiting;
        }
    }
    public int LearningCompletedGroups { get { return learningPractice == null ? 0 : learningPractice.CompletedGroups; } }
    public int LearningTotalGroups { get { return learningPractice == null ? 0 : learningPractice.TotalGroups; } }
    public string PendingNotes
    {
        get
        {
            if (learningPractice == null) return "";
            return String.Join(" + ", Array.ConvertAll(learningPractice.PendingPitches, MidiNotes.Format));
        }
    }
    private bool LearningReady
    {
        get
        {
            var piano = currentStrategy as PianoStrategy;
            return piano != null && !(ProjectConfig.Settings.enableMultiplayer && ProjectConfig.Settings.useMultiplayer) &&
                piano.MidiFeedback != null && piano.MidiFeedback.IsCalibrated;
        }
    }

    public bool SetLearningMode(bool enabled)
    {
        if (!CanSelectInstrument || (enabled && !LearningReady)) return false;
        LearningMode = enabled;
        learningPractice = null;
        return true;
    }

    public void SetReferenceSoundEnabled(bool enabled)
    {
        ReferenceSoundEnabled = enabled;
        if (referenceAudio != null) referenceAudio.SoundEnabled = enabled;
    }

    public void ReceivePracticeNote(int pitch, bool on)
    {
        if (!LearningMode || learningPractice == null || !InputAllowed || !LearningReady) return;
        learningPractice.Receive(pitch, on, Time.time, playing);
    }

    public void OnPracticeInputReset()
    {
        if (!LearningMode) return;
        if (learningPractice != null) learningPractice.ResetInput();
        CancelCountdown();
        Pause();
        RestorePlaybackMenu();
    }
    private float songStartTime;
    public bool playing { get { return State == PlaybackState.Playing; } }
    public bool quit = false;
    private float totalPauseTime = 0; // seconds we spent paused

    private float pauseBegin = 0;

    public static event Action<float> OnResume;
    public static event Action OnPause;
    public static event Action OnQuit;
    public event Action CountdownCancelled;
    public event Action InputInterrupted;


    // We count active notes to know when the song is truly "done" visually
    private int activeNoteCount = 0;

    // Call this from MenuController when selecting the instrument
    public void SetStrategy(IGameplayStrategy strategy)
    {
        if (!CanSelectInstrument || strategy == null) return;
        // Unsubscribe from old strategy if exists
        if (currentStrategy != null)
        {
            currentStrategy.OnNoteFinished -= HandleNoteFinished;
        }

        currentStrategy = strategy;
        if (!(strategy is PianoStrategy)) { LearningMode = false; learningPractice = null; }
        ProjectConfig.Settings.instrument = strategy is PianoStrategy ? "piano" : "bongos";

        if (currentStrategy != null)
        {
            currentStrategy.OnNoteFinished += HandleNoteFinished;
        }
    }

    private void HandleNoteFinished()
    {
        activeNoteCount--;
    }

    public void Play()
    {
        if (State != PlaybackState.Idle || !InputAllowed || !LoadedSongAllowed || loadedNotes.Count == 0 || gameSpeed <= 0 ||
            (LearningMode && !LearningReady)) return;
        if (currentStrategy == null || !currentStrategy.IsInstrumentReady())
        {
            Debug.LogError("Cannot play: Instrument not defined or Strategy missing.");
            return;
        }

        // Use the strategy's specific lookahead time (FallTime or ApproachTime)
        songStartTime = Time.time + currentStrategy.ApproachTime;
        State = PlaybackState.Playing;
        ProjectConfig.Settings.playing = true;
        totalPauseTime = 0;
        quit = false;
        notes = loadedNotes.ConvertAll(n => new NoteEvent { key = n.key, midiNumber = n.midiNumber,
            time = n.time, duration = n.duration, keyIndex = n.keyIndex });
        foreach (var n in notes) currentStrategy.PreprocessNote(n);
        learningPractice = null;
        if (LearningMode)
        {
            var pianoInput = ((PianoStrategy)currentStrategy).MidiFeedback;
            var strikes = notes.ConvertAll(n => new LearningPractice.Strike(n.keyIndex, n.time / gameSpeed));
            learningPractice = new LearningPractice(strikes, pianoInput.FirstNote, pianoInput.LastNote,
                -currentStrategy.ApproachTime, Time.time);
        }
        if (IsPiano)
        {
            if (referenceAudio == null) referenceAudio = GetComponent<MidiReferenceAudio>();
            if (referenceAudio == null) referenceAudio = gameObject.AddComponent<MidiReferenceAudio>();
            referenceAudio.SoundEnabled = ReferenceSoundEnabled;
            referenceAudio.BeginSong(notes, gameSpeed);
            referenceAudio.Sync(SongTime, true);
        }
        // A song may be loaded before the user changes instruments.
        if (currentStrategy is BongoStrategy bongos && notes.Count > 0)
        {
            int average = 0;
            foreach (var n in notes) average += n.keyIndex;
            bongos.average_key = average / notes.Count;
        }
        lastNoteTime = 0;
        foreach (var n in notes) lastNoteTime = Mathf.Max(lastNoteTime, n.time / gameSpeed);
        activeNoteCount = 0;
        if (currentStrategy is PianoStrategy piano && piano.MidiFeedback != null)
            piano.MidiFeedback.BeginSong(notes, gameSpeed);

        //StartCoroutine(PlayAudioWithDelay(currentStrategy.ApproachTime));
    }

    public void Pause()
    {
        if (!HasSession || !playing) return;
        if (learningPractice != null) { learningPractice.Advance(Time.time, true); learningPractice.ResetInput(); }
        ProjectConfig.Settings.playing = false;
        State = PlaybackState.Paused;
        pauseBegin = Time.time;
        if (referenceAudio != null) referenceAudio.Sync(SongTime, false);
        OnPause?.Invoke();
    }

    public void Resume()
    {
        if (State != PlaybackState.Paused || !InputAllowed || !LoadedSongAllowed || (LearningMode && !LearningReady)) return;
        if (learningPractice != null) learningPractice.Advance(Time.time, false);
        ProjectConfig.Settings.playing = true;
        State = PlaybackState.Playing;
        float curPauseTime = Time.time - pauseBegin;
        totalPauseTime += curPauseTime;
        if (referenceAudio != null) referenceAudio.Sync(SongTime, true);
        OnResume?.Invoke(curPauseTime);
    }

    public void Quit()
    {
        CancelCountdown();
        quit = true;
        OnQuit?.Invoke();
        OnFinished();
    }

    public int BeginCountdown(bool resume)
    {
        if (!InputAllowed || !LoadedSongAllowed || (LearningMode && !LearningReady)) return -1;
        if (resume)
        {
            if (State != PlaybackState.Paused) return -1;
            State = PlaybackState.Resuming;
        }
        else
        {
            if (State != PlaybackState.Idle || loadedNotes.Count == 0 || gameSpeed <= 0 ||
                currentStrategy == null || !currentStrategy.IsInstrumentReady()) return -1;
            State = PlaybackState.Starting;
        }
        return ++playbackGeneration;
    }

    public bool CompleteCountdown(int generation, bool resume)
    {
        if (generation != playbackGeneration || !InputAllowed ||
            State != (resume ? PlaybackState.Resuming : PlaybackState.Starting)) return false;
        State = resume ? PlaybackState.Paused : PlaybackState.Idle;
        if (resume) Resume(); else Play();
        return playing;
    }

    public void CancelCountdown()
    {
        if (State != PlaybackState.Starting && State != PlaybackState.Resuming) return;
        State = State == PlaybackState.Resuming ? PlaybackState.Paused : PlaybackState.Idle;
        ++playbackGeneration;
        CountdownCancelled?.Invoke();
        RestorePlaybackMenu();
    }

    private void OnEnable()
    {
        OVRManager.InputFocusLost += HandleHeadsetFocusLost;
        OVRManager.InputFocusAcquired += HandleHeadsetFocusAcquired;
    }

    private void OnDisable()
    {
        OVRManager.InputFocusLost -= HandleHeadsetFocusLost;
        OVRManager.InputFocusAcquired -= HandleHeadsetFocusAcquired;
        InterruptPlayback();
    }

    private void HandleHeadsetFocusLost() { SetHeadsetFocused(false); }
    private void HandleHeadsetFocusAcquired() { SetHeadsetFocused(true); }
    private void OnApplicationPause(bool paused) { SetApplicationPaused(paused); }
    private void OnApplicationFocus(bool focused) { SetApplicationFocused(focused); }

    public void SetApplicationPaused(bool paused)
    {
        if (applicationPaused == paused) return;
        applicationPaused = paused;
        if (paused) InterruptPlayback();
    }

    public void SetApplicationFocused(bool focused)
    {
        if (applicationFocused == focused) return;
        applicationFocused = focused;
        if (!focused) InterruptPlayback();
    }

    public void SetHeadsetFocused(bool focused)
    {
        if (headsetFocused == focused) return;
        headsetFocused = focused;
        if (!focused) InterruptPlayback();
    }

    private void InterruptPlayback()
    {
        bool hadPlayback = State != PlaybackState.Idle;
        CancelCountdown();
        InputInterrupted?.Invoke();
        // Multiplayer pause/resume is coordinated through RPCs. A local focus
        // callback must not freeze only this peer's song clock.
        bool multiplayer = ProjectConfig.Settings.enableMultiplayer && ProjectConfig.Settings.useMultiplayer;
        if (multiplayer) return;
        Pause();
        if (hadPlayback) RestorePlaybackMenu();
    }

    public void RestorePlaybackMenu()
    {
        if (menucontroller == null) return;
        if (State != PlaybackState.Idle && State != PlaybackState.Paused) return;
        bool multiplayer = ProjectConfig.Settings.enableMultiplayer && ProjectConfig.Settings.useMultiplayer;
        if (HasSession)
        {
            if (multiplayer) menucontroller.ShowMultiplayerPauseMenu(ProjectConfig.Settings.master);
            else menucontroller.ShowSingleplayerPauseMenu();
        }
        else
        {
            if (multiplayer) menucontroller.ShowMultiplayerLobbyMenu();
            else menucontroller.ShowSinglePlayerLobbyMenu();
        }
    }

    IEnumerator PlayAudioWithDelay(float delay)
    {
        yield return new WaitForSeconds(delay);
        //audioSource.pitch = gameSpeed;
        //audioSource.Play();
        // Resync to ensure exact timing
        songStartTime = Time.time;
    }

    public void updateSong(SongData songdata)
    {
        StartCoroutine(updateSongRoutine(songdata));
    }

    public IEnumerator updateSongRoutine(SongData songdata)
    {
        if (songdata == null || songdata.PartialSongData == null) yield break;
        if (songdata.PartialSongData.IsExternal && ProjectConfig.Settings.enableMultiplayer && ProjectConfig.Settings.useMultiplayer)
        {
            Debug.LogWarning("Personal MIDI songs are only available in single-player.");
            yield break;
        }
        int generation = ++loadGeneration;
        CancelCountdown();
        if (HasSession) Quit();
        loadedNotes.Clear();
        loadedSongIsExternal = false;
        Debug.Log("Processing song: " + songdata.PartialSongData.JsonPath);
        string jsonContent = songdata.PartialSongData.InlineJson;

        if (!songdata.PartialSongData.IsExternal)
        {
            string fullPath = Path.Combine(Application.streamingAssetsPath, songdata.PartialSongData.JsonPath);
            // Android/Desktop compatible bundled loading. External data is a startup snapshot.
#if UNITY_ANDROID && !UNITY_EDITOR
            using (UnityWebRequest www = UnityWebRequest.Get(fullPath))
            {
                yield return www.SendWebRequest();
                if (www.result == UnityWebRequest.Result.Success) jsonContent = www.downloadHandler.text;
            }
#else
            try { if (File.Exists(fullPath)) jsonContent = File.ReadAllText(fullPath); }
            catch (Exception e) { Debug.LogWarning("Could not read song: " + e.Message); }
#endif
        }
        yield return null;

        if (generation != loadGeneration) yield break;
        if (songdata.PartialSongData.IsExternal && ProjectConfig.Settings.enableMultiplayer && ProjectConfig.Settings.useMultiplayer)
            yield break;
        if (!string.IsNullOrEmpty(jsonContent))
        {
            try
            {
                var parsed = JsonConvert.DeserializeObject<List<NoteEvent>>(jsonContent);
                if (parsed == null || parsed.Exists(n => n == null || float.IsNaN(n.time) || float.IsInfinity(n.time) ||
                    float.IsNaN(n.duration) || float.IsInfinity(n.duration) || n.time < 0 || n.duration < 0))
                    throw new FormatException("Invalid note data.");
                loadedNotes = parsed.ConvertAll(n => new NoteEvent { key = n.key, midiNumber = n.midiNumber,
                    time = n.time, duration = n.duration });
                loadedSongIsExternal = songdata.PartialSongData.IsExternal;
            }
            catch (Exception e) { Debug.LogWarning("Could not load song: " + e.Message); }
        }
    }

    void Update()
    {
        if (referenceAudio != null) referenceAudio.Sync(SongTime, playing);
        if (!playing) return;

        // Check if song is over
        if (notes.Count == 0 && activeNoteCount <= 0 && SongTime >= lastNoteTime + 0.30)
        {
            OnFinished();
            return;
        }

        double elapsed = SongTime;

        foreach (var note in notes.ToArray())
        {
            // DELEGATE: Use strategy.ApproachTime instead of hardcoded fallTime
            if (elapsed >= note.time / gameSpeed - currentStrategy.ApproachTime)
            {
                note.duration /= gameSpeed;

                // DELEGATE: The strategy spawns the actual object
                activeNoteCount++;
                currentStrategy.SpawnNote(note, gameSpeed);

                notes.Remove(note);
            }
        }
    }

    public void OnFinished()
    {
        CancelCountdown();
        if (referenceAudio != null) referenceAudio.Stop();
        if (currentStrategy is PianoStrategy piano && piano.MidiFeedback != null)
            piano.MidiFeedback.EndSong(!quit);
        State = PlaybackState.Idle;
        learningPractice = null;
        ProjectConfig.Settings.playing = false;
        totalPauseTime = 0;
        menucontroller.ShowSongMenu();
        return;
    }
}
