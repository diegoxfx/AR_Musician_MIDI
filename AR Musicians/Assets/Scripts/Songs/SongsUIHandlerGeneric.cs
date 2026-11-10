using System;
using System.Collections.Generic;
using System.Linq;
using TMPro;
using UnityEngine;

public class SongsUIHandlerGeneric : MonoBehaviour
{
    [SerializeField] private TMP_Text songTitleTMP;
    [SerializeField] private TMP_Text songArtistTMP;
    [SerializeField] private TMP_Text songReleaseYearTMP;
    [SerializeField] private TMP_Text songDurationTMP;
    [SerializeField] private TMP_Text songDifficultyTMP;
    public RhythmGameManager manager;

    private List<SongData> allSongs = new List<SongData>();
    private List<SongData> songsData = new List<SongData>();
    private int localPos;
    private bool? shownMultiplayer;
    private bool catalogLoaded;
    private SongData selectedSong;
    public static event Action<int> OnNewPosition;
    public static event Action OnSelectSong;

    private static bool IsMultiplayer
    {
        get { return ProjectConfig.Settings.enableMultiplayer && ProjectConfig.Settings.useMultiplayer; }
    }

    internal SongData SelectedSong
    {
        get { RefreshVisibleSongs(); return selectedSong; }
        set
        {
            RefreshVisibleSongs();
            if (value != null && !songsData.Contains(value)) return;
            selectedSong = value;
            if (value != null) localPos = songsData.IndexOf(value);
            UpdateUI();
        }
    }

    private void OnEnable()
    {
        SongLoader.OnSongsLoaded += OnSongsLoaded;
        if (SongLoader.CurrentSongs != null) OnSongsLoaded(SongLoader.CurrentSongs);
        else UpdateUI();
    }

    private void OnDisable() { SongLoader.OnSongsLoaded -= OnSongsLoaded; }
    private void Update() { RefreshVisibleSongs(); }

    private void OnSongsLoaded(LinkedList<SongData> songs)
    {
        allSongs = songs == null ? new List<SongData>() : songs.ToList();
        catalogLoaded = true;
        RefreshVisibleSongs(true);
    }

    private void RefreshVisibleSongs(bool force = false)
    {
        bool multiplayer = IsMultiplayer;
        if (!force && shownMultiplayer == multiplayer) return;
        shownMultiplayer = multiplayer;
        // Peers only synchronize numeric indices. Local files cannot join that list.
        songsData = allSongs.Where(song => !multiplayer || !song.IsExternal).ToList();
        localPos = 0;
        selectedSong = songsData.Count == 0 ? null : songsData[0];
        UpdateUI();
    }

    public void OnSongSelected()
    {
        SongData song = SelectedSong;
        if (song == null || manager == null || !manager.CanSelectInstrument) return;
        StartCoroutine(manager.updateSongRoutine(song));
        // Navigation belongs to this guarded action, not an unconditional button listener.
        manager.menucontroller?.postSongChoiceMenu();
        OnSelectSong?.Invoke();
    }

    public void OnNextSongSelected() { MoveSelection(1); }
    public void OnPreviousSongSelected() { MoveSelection(-1); }

    private void MoveSelection(int direction)
    {
        RefreshVisibleSongs();
        if (songsData.Count == 0) return;
        localPos = (localPos + direction + songsData.Count) % songsData.Count;
        selectedSong = songsData[localPos];
        UpdateUI();
        OnNewPosition?.Invoke(localPos);
    }

    public SongData getSongData(int pos)
    {
        RefreshVisibleSongs();
        return pos >= 0 && pos < songsData.Count ? songsData[pos] : null;
    }

    private void UpdateUI()
    {
        if (selectedSong == null)
        {
            SetText(songTitleTMP, catalogLoaded ? "No songs available" : "Loading songs...");
            SetText(songArtistTMP, "");
            SetText(songReleaseYearTMP, "");
            SetText(songDurationTMP, "");
            SetText(songDifficultyTMP, "");
            return;
        }
        SetText(songTitleTMP, selectedSong.title);
        SetText(songArtistTMP, string.Join(", ", selectedSong.artists) + (selectedSong.IsExternal ? " (Personal MIDI)" : ""));
        SetText(songReleaseYearTMP, selectedSong.releaseYear < 0 ? "" : Convert.ToString(selectedSong.releaseYear));
        SetText(songDurationTMP, Convert.ToString(selectedSong.duration));
        SetText(songDifficultyTMP, selectedSong.difficulty.ToString());
    }

    private static void SetText(TMP_Text label, string text)
    {
        if (label == null) return;
        label.richText = false;
        label.text = text;
    }
}
