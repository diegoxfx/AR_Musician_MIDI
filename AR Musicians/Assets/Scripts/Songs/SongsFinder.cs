using NUnit.Framework;
using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using UnityEngine;
using UnityEngine.Networking;

public class PartialSongData
{
    /// <summary>
    /// The path to this MIDI song's corresponding `.json` file, that contains
    /// the data of its notes, required to display them to the user.
    /// </summary>
    public string JsonPath { get; private set; }
    /// <summary>
    /// The path to this MIDI song's corresponding metadata's `.json` file, that contains
    /// the song's metadata of its notes (see <see cref="ISongMetadata"/> ).
    /// A `.json` file is considered corresponding with a song file `song.midi` if
    /// it has name `song{METADATA_SUFFIX}.json` (see <see cref="SongsFinder.METADATA_SUFFIX"/>) and is 
    /// found within the same directory.
    /// </summary>
    public string MetadataPath { get; private set; }
    /// <summary>
    /// Whether this song's `.midi` file has a corresponding metadata `.json` file.
    /// A `.json` file is considered corresponding with a song file `song.midi` if
    /// it has name `song{METADATA_SUFFIX}.json` (see <see cref="SongsFinder.METADATA_SUFFIX"/>) and is 
    /// found within the same directory
    /// </summary>
    public bool HasCorrespondingMetadata { get; private set; }

    public PartialSongData(string jsonPath, string metadataPath, bool hasCorrespondingMetadata)
    {
        JsonPath = jsonPath;
        MetadataPath = metadataPath;
        HasCorrespondingMetadata = hasCorrespondingMetadata;
    }

    public PartialSongData(PartialSongData source) : this(source.JsonPath, source.MetadataPath, source.HasCorrespondingMetadata) { }
}

public class SongsFinder : MonoBehaviour
{
    public const string METADATA_SUFFIX = "_metadata";

    /// <summary>
    /// Finds MIDI files and their corresponding JSON data on Android/Quest by reading a pre-generated manifest.
    /// </summary>
    private IEnumerator FindMidisAndroid(string streamingAssetsPath, LinkedList<PartialSongData> foundSongs)
    {
        string manifestUrl = Path.Combine(streamingAssetsPath, "file_manifest.json");

        using (UnityWebRequest www = UnityWebRequest.Get(manifestUrl))
        {
            yield return www.SendWebRequest();

            if (www.result != UnityWebRequest.Result.Success)
            {
                Debug.LogError("Failed to load file manifest: " + www.error);
                yield break;
            }

            string json = www.downloadHandler.text;
            FileList manifest = JsonUtility.FromJson<FileList>(json);

            // Create a fast lookup set for all files in the build
            var allPaths = new HashSet<string>(manifest.paths);

            // Filter specifically for your song JSONs
            foreach (string filePath in allPaths)
            {
                if (!filePath.EndsWith(".json")) continue;
                if (filePath.Contains("file_manifest") || filePath.Contains(METADATA_SUFFIX)) continue;

                // We assume the manifest paths are relative to StreamingAssets (e.g. "Music/Song.json")
                string relativeJsonPath = filePath;

                string directory = Path.GetDirectoryName(relativeJsonPath);
                string fileNameNoExt = Path.GetFileNameWithoutExtension(relativeJsonPath);

                string relativeMetadataPath = "";
                if (string.IsNullOrEmpty(directory))
                {
                    relativeMetadataPath = fileNameNoExt + METADATA_SUFFIX + ".json";
                }
                else
                {
                    relativeMetadataPath = Path.Combine(directory, fileNameNoExt + METADATA_SUFFIX + ".json").Replace("\\", "/");
                }

                // Check if metadata exists in our MANIFEST list (not on disk)
                bool hasMetadata = allPaths.Contains(relativeMetadataPath);

                foundSongs.AddLast(new PartialSongData(
                    relativeJsonPath,
                    hasMetadata ? relativeMetadataPath : null,
                    hasMetadata
                ));
            }
        }
    }

    /// <summary>
    /// Finds Json files on Windows/Editor using direct file access.
    /// </summary>
    private IEnumerator FindJsonsWindows(string streamingAssetsPath, LinkedList<PartialSongData> foundSongs)
    {
        // If you're wondering why the `.Where` clause is necessary, see https://learn.microsoft.com/en-us/dotnet/api/system.io.directory.getfiles?view=net-9.0 and its remarks
        // Basically, calling `Directory.GetFiles` with searchPattern `*.abc` returns both `*.abc` files, as well as `*.abc*` files.
        string[] jsonFiles = Directory.GetFiles(streamingAssetsPath, "*.json", SearchOption.AllDirectories).Where(file => file.EndsWith(".json") || file.EndsWith(".midi")).ToArray();
        foreach (string jsonFilePath in jsonFiles)
        {
            if (jsonFilePath.Contains("metadata") || jsonFilePath.Contains("file_manifest"))
                continue;

            string fileNameWithoutExt = Path.GetFileNameWithoutExtension(jsonFilePath);
            string relativePath = jsonFilePath.Substring(streamingAssetsPath.Length + 1).Replace('\\', '/');

            string expectedMetadataPath = Path.Combine(
                Path.GetDirectoryName(jsonFilePath),
                fileNameWithoutExt + METADATA_SUFFIX + ".json"
            );

            bool hasMetadata = File.Exists(expectedMetadataPath);

            foundSongs.AddLast(
                new PartialSongData(
                    relativePath,
                    hasMetadata ? expectedMetadataPath : null,
                    hasMetadata
                )
            );
        }

        yield return null; // Yield once to maintain coroutine structure.
    }

    public IEnumerator LoadJsonFile(string relativePath, Action<string> callback)
    {
        string fullPath = Path.Combine(Application.streamingAssetsPath, relativePath);

#if UNITY_ANDROID && !UNITY_EDITOR
    using (UnityWebRequest www = UnityWebRequest.Get(fullPath))
    {
        yield return www.SendWebRequest();

        if (www.result != UnityWebRequest.Result.Success)
        {
            Debug.LogError("Failed to read json: " + fullPath);
            callback(null);
            yield break;
        }

        callback(www.downloadHandler.text);
    }
#else
        if (!File.Exists(fullPath))
        {
            Debug.LogError("File does not exist: " + fullPath);
            callback(null);
            yield break;
        }

        callback(File.ReadAllText(fullPath));
        yield return null;
#endif
    }

    /// <summary>
    /// Looks through the `StreamingAssets` folder to find all .json files.
    /// Delegates to platform-specific methods.
    /// </summary>
    public IEnumerator FindJsons(System.Action<LinkedList<PartialSongData>> callback)
    {
        var foundSongs = new LinkedList<PartialSongData>();
        string streamingAssetsPath = Application.streamingAssetsPath;

#if UNITY_ANDROID && !UNITY_EDITOR
        yield return FindMidisAndroid(streamingAssetsPath, foundSongs);
#else
        yield return FindJsonsWindows(streamingAssetsPath, foundSongs);
#endif

        // The callback is called exactly once, after the appropriate helper method has finished.
        callback(foundSongs);
    }
}
