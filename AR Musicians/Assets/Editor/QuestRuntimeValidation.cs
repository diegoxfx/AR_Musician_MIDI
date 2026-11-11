using System;
using System.IO;
using System.Linq;
using System.Reflection;
using UnityEditor;
using UnityEditor.Build;
using UnityEditor.SceneManagement;
using UnityEngine;

/// <summary>Checks the actual scene assets, beyond the standalone test doubles.</summary>
public static class QuestRuntimeValidation
{
    public static PianoStrategy ValidateSceneAssets()
    {
        var strategies = UnityEngine.Object.FindObjectsByType<PianoStrategy>(
            FindObjectsInactive.Include, FindObjectsSortMode.None);
        Require(strategies.Length == 1, "Expected one PianoStrategy in MainScene.");
        var piano = strategies.Single();
        Require(piano.plane != null, "The piano strategy has no keyboard plane.");
        Require(piano.noteCubePrefab != null, "The piano note prefab is missing.");
        var renderer = piano.noteCubePrefab.GetComponent<Renderer>();
        Require(renderer != null, "The piano note prefab has no root Renderer.");
        Require(renderer.sharedMaterial != null, "The piano note prefab material is missing.");
        Require(renderer.sharedMaterial.shader != null, "The piano note material shader is missing.");
        Require(AssetDatabase.Contains(renderer.sharedMaterial), "The piano note material must be a saved asset.");
        Debug.Log("[QuestRuntimeValidation] Piano note material: " +
            AssetDatabase.GetAssetPath(renderer.sharedMaterial) + "; shader: " + renderer.sharedMaterial.shader.name);
        return piano;
    }

    // Unity -batchmode -quit -projectPath ... -buildTarget Android
    //   -executeMethod QuestRuntimeValidation.Run -validationOutput <absolute path>
    public static void Run()
    {
        GameObject host = null;
        try
        {
            EditorSceneManager.OpenScene("Assets/Scenes/MainScene.unity", OpenSceneMode.Single);
            var piano = ValidateSceneAssets();
            host = new GameObject("MIDI initialization validation");
            var feedback = host.AddComponent<PianoMidiFeedback>();
            feedback.Initialize(piano);
            Require(feedback.IsInitialized, "MIDI feedback did not finish initialization with the real scene assets.");
            var labels = host.GetComponentsInChildren<TextMesh>(true);
            Require(labels.Length == 1 && labels[0].font != null, "The MIDI status font was not initialized.");
            Require(labels[0].GetComponent<MeshRenderer>()?.sharedMaterial != null,
                "The MIDI status text material was not initialized.");
            string result = "PASS: real MainScene, piano prefab material/shader, and MIDI feedback initialization.\n" +
                "Editor validation does not verify headset input, spatial permissions, or USB hardware.\n";
            string[] args = Environment.GetCommandLineArgs();
            int outputIndex = Array.IndexOf(args, "-validationOutput");
            if (outputIndex >= 0 && outputIndex + 1 < args.Length)
                File.WriteAllText(args[outputIndex + 1], result);
            Debug.Log("[QuestRuntimeValidation] " + result);
        }
        finally
        {
            if (host != null)
            {
                // Runtime uses deferred Destroy; this asset smoke check runs in Edit Mode.
                // Dispose only the objects created by Initialize before its OnDestroy runs.
                var feedback = host.GetComponent<PianoMidiFeedback>();
                const BindingFlags flags = BindingFlags.Instance | BindingFlags.NonPublic;
                var materialField = typeof(PianoMidiFeedback).GetField("markerMaterial", flags);
                var material = materialField.GetValue(feedback) as Material;
                if (material != null) UnityEngine.Object.DestroyImmediate(material);
                materialField.SetValue(feedback, null);
                var labelField = typeof(PianoMidiFeedback).GetField("label", flags);
                var label = labelField.GetValue(feedback) as TextMesh;
                if (label != null) UnityEngine.Object.DestroyImmediate(label.gameObject);
                labelField.SetValue(feedback, null);
                UnityEngine.Object.DestroyImmediate(host);
            }
        }
    }

    private static void Require(bool condition, string message)
    {
        if (!condition) throw new BuildFailedException(message);
    }
}
