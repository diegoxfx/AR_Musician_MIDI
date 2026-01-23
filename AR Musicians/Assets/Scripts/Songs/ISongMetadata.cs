
using Assets.Scripts.Songs;
using Newtonsoft.Json;
using Newtonsoft.Json.Converters;

[System.Serializable]
public abstract class ISongMetadata
{
    /// <summary>
    /// Full title of the song
    /// </summary>
    public string title;
    /// <summary>
    /// Array of artist names
    /// </summary>
    public string[] artists;
    /// <summary>
    /// Release year of the first edition of the track
    /// </summary>
    public short releaseYear;
    /// <summary>
    /// Duration in seconds of the track
    /// </summary>
    public uint duration;
    /// <summary>
    /// Difficulty of playing the track
    /// </summary>
    public SongDifficulty difficulty;
}
