using Assets.Scripts.Songs;
using Melanchall.DryWetMidi.MusicTheory;
using Meta.Voice.Net.WebSockets;
using Newtonsoft.Json;
using NUnit.Framework.Constraints;
using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using UnityEngine;
using UnityEngine.Networking;

internal class ISongJson : ISongMetadata { };

public class SongData : ISongMetadata
{
    public PartialSongData PartialSongData { get; private set; }

    public SongData(string title, string[] artists, short releaseYear, uint duration, SongDifficulty difficulty, PartialSongData partialSongData)
    {
        this.title = title;
        this.releaseYear = releaseYear;
        this.duration = duration;
        this.difficulty = difficulty;
        PartialSongData = new PartialSongData(partialSongData);

        // Copy array over to prevent side effects in client
        this.artists = new string[artists.Length];
        Array.Copy(artists, this.artists, artists.Length);
    }

    public SongData(ISongMetadata metadata, PartialSongData partialSongData) : this(metadata.title, metadata.artists, metadata.releaseYear, metadata.duration, metadata.difficulty, partialSongData) { }

    public override string ToString()
    {
        return $"{string.Join(", ", this.artists)} - {this.title} [{this.releaseYear}] ({this.duration}s, {this.difficulty})";
    }
}

public class SongLoader : MonoBehaviour
{
    #region Private members
    private LinkedList<SongData> songs;
    internal static Action<LinkedList<SongData>> OnSongsLoaded;
    #endregion

    #region Serialized members
    [SerializeField]
    private SongsFinder _songsFinder;
    #endregion

    //private IEnumerator FetchSongs()
    //{
    //    if (_songsFinder == null)
    //    {
    //        throw new MissingComponentException("Could not find necessary 'SongsFinder' component.");
    //    }
    //    LinkedList<PartialSongData> foundSongs = new();
    //    // Fetch songs from SongsFinder
    //    yield return StartCoroutine(
    //        _songsFinder.FindJsons(foundJsons => foundSongs = foundJsons)
    //    );
    //    // Use data from foundSongs to populate new list of SongData
    //    LinkedList<SongData> songsData = new();
    //    songs = new();
    //    foreach (PartialSongData song in foundSongs)
    //    {
    //        SongData songData;
    //        if (song.HasCorrespondingMetadata)
    //        {
    //            // Read the metadata JSON
    //            string json = File.ReadAllText(song.MetadataPath);
    //            ISongJson songJson = JsonConvert.DeserializeObject<ISongJson>(json);
    //            songData = new SongData(
    //                songJson,
    //                song
    //            );
    //        }
    //        else
    //        {
    //            songData = new SongData(
    //                Path.GetFileNameWithoutExtension(song.JsonPath),
    //                new string[] { "n/a" },
    //                -1,
    //                0,
    //                SongDifficulty.Professional,
    //                song
    //            );
    //        }
    //        songs.AddLast(songData);
    //    }
    //    OnSongsLoaded?.Invoke(songs);
    //}
    private IEnumerator FetchSongs()
    {
        if (_songsFinder == null)
        {
            throw new MissingComponentException("Could not find necessary 'SongsFinder' component.");
        }

        LinkedList<PartialSongData> foundSongs = new LinkedList<PartialSongData>();

        // 1. Fetch the list of songs (Wait for SongsFinder to finish)
        yield return StartCoroutine(
            _songsFinder.FindJsons(foundJsons => foundSongs = foundJsons)
        );

        LinkedList<SongData> songsData = new LinkedList<SongData>();
        songs = new LinkedList<SongData>();

        // 2. Iterate through the found songs and load metadata
        foreach (PartialSongData song in foundSongs)
        {
            SongData songData = null; // We will assign this based on success/failure
            string jsonContent = null;

            if (song.HasCorrespondingMetadata)
            {
                // Note: songs.MetadataPath comes from SongsFinder. 
                // If it is relative (e.g. "Music/song_meta.json"), we must combine it.
                // If SongsFinder returns full paths on Windows, Path.Combine handles it, 
                // but on Android we usually need the full StreamingAssets path.
                string fullPath = Path.Combine(Application.streamingAssetsPath, song.MetadataPath);

#if UNITY_ANDROID && !UNITY_EDITOR
            // --- ANDROID LOGIC ---
            using (UnityWebRequest www = UnityWebRequest.Get(fullPath))
            {
                // This pauses the loop until this specific file is read
                yield return www.SendWebRequest();

                if (www.result == UnityWebRequest.Result.Success)
                {
                    jsonContent = www.downloadHandler.text;
                }
                else
                {
                    Debug.LogWarning($"Failed to load metadata for {song.JsonPath}: {www.error}");
                }
            }
#else
                // --- WINDOWS / EDITOR LOGIC ---
                if (File.Exists(fullPath))
                {
                    jsonContent = File.ReadAllText(fullPath);
                }
#endif

                // If we successfully got content (from either platform), deserialize it
                if (!string.IsNullOrEmpty(jsonContent))
                {
                    try
                    {
                        ISongJson songJson = JsonConvert.DeserializeObject<ISongJson>(jsonContent);
                        songData = new SongData(songJson, song);
                    }
                    catch (System.Exception ex)
                    {
                        Debug.LogError($"JSON Parsing Error for {song.MetadataPath}: {ex.Message}");
                    }
                }
            }

            // 3. Fallback: If no metadata file existed, OR if loading failed above
            if (songData == null)
            {
                songData = new SongData(
                    Path.GetFileNameWithoutExtension(song.JsonPath),
                    new string[] { "n/a" },
                    -1,
                    0,
                    SongDifficulty.Professional,
                    song
                );
            }

            songs.AddLast(songData);
        }

        OnSongsLoaded?.Invoke(songs);
    }

    private void Awake()
    {
        StartCoroutine(FetchSongs());
    }
}
