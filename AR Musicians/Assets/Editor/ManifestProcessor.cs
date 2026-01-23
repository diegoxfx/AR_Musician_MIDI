using System.Collections.Generic;
using System.IO;
using System.Linq;
using UnityEditor;
using UnityEditor.Build;
using UnityEditor.Build.Reporting;
using UnityEngine;

// This script runs before a build is made.
public static class ManifestProcessor
{

    private static void ConvertMidiFiles(string streamingAssetsPath)
    {
        // Find all MIDI files
        string[] midiFiles = Directory.GetFiles(streamingAssetsPath, "*.mid", SearchOption.AllDirectories);

        bool newFilesCreated = false;
        foreach (string midiPath in midiFiles)
        {
            string jsonPath = Path.ChangeExtension(midiPath, ".json");

            // If the corresponding JSON doesn't exist, create it.
            if (!File.Exists(jsonPath))
            {
                Debug.Log($"Conversion needed. Converting '{Path.GetFileName(midiPath)}' to JSON...");
                try
                {
                    Assets.Scripts.Songs.MidiUtils.ConvertMidiToJson(midiPath, jsonPath);
                    newFilesCreated = true;
                }
                catch (System.Exception e)
                {
                    Debug.LogError($"Failed to convert {midiPath}. Error: {e.Message}");
                }
            }
        }

        // If we created new files, we MUST refresh the AssetDatabase.
        // This makes Unity aware of them so they can be included in the build.
        if (newFilesCreated)
        {
            Debug.Log("New JSON files were created. Refreshing Asset Database...");
            AssetDatabase.Refresh();
        }
    }

    private static void BuildManifestFromStreamingAssets(string streamingAssetsPath, string manifestFilePath)
    {
        // Find all relevant files recursively
        var allFiles = Directory.GetFiles(streamingAssetsPath, "*.*", SearchOption.AllDirectories)
            .Where(p => p.EndsWith(".json")); // Include all .json files for simplicity

        // We need paths relative to the StreamingAssets folder for cross-platform compatibility
        List<string> relativePaths = new List<string>();
        foreach (string file in allFiles)
        {
            // Make sure to use forward slashes for universal compatibility
            string relativePath = file.Substring(streamingAssetsPath.Length + 1).Replace('\\', '/');
            relativePaths.Add(relativePath);
        }

        // Serialize the list of paths to JSON and write it to the manifest file
        string json = JsonUtility.ToJson(new FileList { paths = relativePaths }, true);
        File.WriteAllText(manifestFilePath, json);

        // Refresh the asset database to ensure the new manifest is included in the build
        AssetDatabase.Refresh();
    }

    public static void RunProcessing()
    {
        Debug.Log("Starting pre-build asset processing...");
        string streamingAssetsPath = Application.streamingAssetsPath;
        string manifestFilePath = Path.Combine(streamingAssetsPath, "file_manifest.json");

        // Ensure the StreamingAssets directory exists
        if (!Directory.Exists(streamingAssetsPath))
        {
            Directory.CreateDirectory(streamingAssetsPath);
        }

        ConvertMidiFiles(streamingAssetsPath);

        BuildManifestFromStreamingAssets(streamingAssetsPath, manifestFilePath);

        Debug.Log("File manifest created successfully at: " + manifestFilePath);
    }

    // This runs when entering Play Mode
    [InitializeOnLoadMethod]
    private static void Init()
    {
        EditorApplication.playModeStateChanged += OnPlayModeChanged;
    }

    private static void OnPlayModeChanged(PlayModeStateChange state)
    {
        if (state == PlayModeStateChange.ExitingEditMode)
        {
            Debug.Log("[StreamingAssets] Running pre-play processing...");
            RunProcessing();
        }
    }
}