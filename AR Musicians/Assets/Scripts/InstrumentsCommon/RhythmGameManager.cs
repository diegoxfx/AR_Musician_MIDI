using System.Collections;
using System.Collections.Generic;
using UnityEngine;
using System.IO;
using Newtonsoft.Json;
using UnityEngine.Networking;
using System;

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
    private int loadGeneration;
    public bool IsPiano { get { return currentStrategy is PianoStrategy; } }
    public bool HasSession { get; private set; }
    private float lastNoteTime;
    public double SongTime { get { return Time.time - songStartTime - totalPauseTime; } }
    private float songStartTime;
    public bool playing = false;
    public bool quit = false;
    private float totalPauseTime = 0; // seconds we spent paused

    private float pauseBegin = 0;

    public static event Action<float> OnResume;
    public static event Action OnPause;
    public static event Action OnQuit;


    // We count active notes to know when the song is truly "done" visually
    private int activeNoteCount = 0;

    // Call this from MenuController when selecting the instrument
    public void SetStrategy(IGameplayStrategy strategy)
    {
        // Unsubscribe from old strategy if exists
        if (currentStrategy != null)
        {
            currentStrategy.OnNoteFinished -= HandleNoteFinished;
        }

        currentStrategy = strategy;

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
        if (HasSession || loadedNotes.Count == 0 || gameSpeed <= 0) return;
        if (currentStrategy == null || !currentStrategy.IsInstrumentReady())
        {
            Debug.LogError("Cannot play: Instrument not defined or Strategy missing.");
            return;
        }

        // Use the strategy's specific lookahead time (FallTime or ApproachTime)
        songStartTime = Time.time + currentStrategy.ApproachTime;
        playing = true;
        ProjectConfig.Settings.playing = true;
        totalPauseTime = 0;
        quit = false;
        HasSession = true;
        notes = loadedNotes.ConvertAll(n => new NoteEvent { key = n.key, midiNumber = n.midiNumber,
            time = n.time, duration = n.duration, keyIndex = n.keyIndex });
        foreach (var n in notes) currentStrategy.PreprocessNote(n);
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
        ProjectConfig.Settings.playing = false;
        playing = false;
        pauseBegin = Time.time;
        OnPause?.Invoke();
    }

    public void Resume()
    {
        if (!HasSession || playing) return;
        ProjectConfig.Settings.playing = true;
        playing = true;
        float curPauseTime = Time.time - pauseBegin;
        totalPauseTime += curPauseTime;
        OnResume?.Invoke(curPauseTime);
    }

    public void Quit()
    {
        quit = true;
        OnQuit?.Invoke();
        OnFinished();
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
        int generation = ++loadGeneration;
        if (HasSession) Quit();
        loadedNotes.Clear();
        Debug.Log("Processing song: " + songdata.PartialSongData.JsonPath);
        string fullPath = Path.Combine(Application.streamingAssetsPath, songdata.PartialSongData.JsonPath);
        string jsonContent = null;

        // Android/Desktop compatible loading
#if UNITY_ANDROID && !UNITY_EDITOR
        using (UnityWebRequest www = UnityWebRequest.Get(fullPath))
        {
            yield return www.SendWebRequest();
            if (www.result == UnityWebRequest.Result.Success) jsonContent = www.downloadHandler.text;
        }
#else
        if (File.Exists(fullPath)) jsonContent = File.ReadAllText(fullPath);
        yield return null;
#endif

        if (generation != loadGeneration) yield break;
        if (!string.IsNullOrEmpty(jsonContent))
        {
            notes = JsonConvert.DeserializeObject<List<NoteEvent>>(jsonContent);
            if (notes == null) yield break;
            loadedNotes = notes.ConvertAll(n => new NoteEvent { key = n.key, midiNumber = n.midiNumber,
                time = n.time, duration = n.duration });

            // DELEGATE: Ask the strategy to interpret the notes (Piano keys vs Bongo sides)
            foreach (var n in notes)
            {
                if (currentStrategy != null)
                    currentStrategy.PreprocessNote(n);
            }
            if (currentStrategy is BongoStrategy && notes.Count > 0)
            {
                int average_key = 0;
                foreach (var n in notes)
                {
                    average_key += n.keyIndex;
                }
                average_key /= notes.Count;
                BongoStrategy strat = (BongoStrategy)currentStrategy;
                strat.average_key = average_key;
            }
        }
    }

    void Update()
    {
        if (!playing) return;

        // Check if song is over
        if (notes.Count == 0 && activeNoteCount <= 0 && SongTime >= lastNoteTime + 0.30)
        {
            OnFinished();
            return;
        }

        float elapsed = Time.time - songStartTime - totalPauseTime;

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
        if (currentStrategy is PianoStrategy piano && piano.MidiFeedback != null)
            piano.MidiFeedback.EndSong(!quit);
        playing = false;
        HasSession = false;
        ProjectConfig.Settings.playing = false;
        totalPauseTime = 0;
        menucontroller.ShowSongMenu();
        return;
    }
}
