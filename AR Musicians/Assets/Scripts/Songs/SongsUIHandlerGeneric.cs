using System;
using System.Collections.Generic;
using System.Linq;
using TMPro;
using UnityEngine;

public class SongsUIHandlerGeneric : MonoBehaviour
{
    #region Serialized fields
    [SerializeField]
    private TMP_Text songTitleTMP;
    [SerializeField]
    private TMP_Text songArtistTMP;
    [SerializeField]
    private TMP_Text songReleaseYearTMP;
    [SerializeField]
    private TMP_Text songDurationTMP;
    [SerializeField]
    private TMP_Text songDifficultyTMP;
    #endregion

    public RhythmGameManager manager;

    #region Private members
    private List<SongData> songsData;
    private int nSongs;
    private int localPos = 0;
    #endregion

    #region Events

    public static event Action<int> OnNewPosition;
    public static event Action OnSelectSong;
    #endregion


    #region Properties
    private SongData selectedSong;

    internal SongData SelectedSong
    {
        get { return selectedSong; }
        set
        {
            Debug.Log($"Selected new song: {value}. Updating song selection UI");
            selectedSong = value;
            UpdateUI();
        }
    }

    #endregion
    private void Awake()
    {
        SongLoader.OnSongsLoaded += OnSongsLoaded;
    }

    #region Event handlers
    private void OnSongsLoaded(LinkedList<SongData> songsData)
    {
        this.songsData = songsData.ToList();
        SelectedSong = this.songsData[0];
        this.nSongs = songsData.Count;
    }

    public void OnSongSelected()
    {
        StartCoroutine(manager.updateSongRoutine(SelectedSong));
        OnSelectSong?.Invoke();
    }

    public void OnNextSongSelected()
    {
        localPos = (localPos + 1) % nSongs;
        SelectedSong = songsData[localPos];
        OnNewPosition?.Invoke(localPos);
    }

    public void OnPreviousSongSelected()
    {
        localPos = (localPos - 1 + nSongs) % nSongs;
        SelectedSong = songsData[localPos];
        OnNewPosition?.Invoke(localPos);
    }

    public SongData getSongData(int pos)
    {
        return songsData[pos];
    }

    private void UpdateUI()
    {
        SongData currentSongData = selectedSong;
        if (currentSongData == null)
        {
            Debug.LogWarning("Expected selected song data. Got null");
            OnNextSongSelected();
            return;
        }
        songTitleTMP.text = currentSongData.title;
        songArtistTMP.text = string.Join(", ", currentSongData.artists);
        songReleaseYearTMP.text = Convert.ToString(currentSongData.releaseYear);
        songDurationTMP.text = Convert.ToString(currentSongData.duration);
        songDifficultyTMP.text = currentSongData.difficulty.ToString();
    }
    #endregion
}
