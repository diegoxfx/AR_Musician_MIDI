using System;
using System.IO;
using Unity.InferenceEngine;
using UnityEditor;
using UnityEditor.Build;
using UnityEditor.Build.Reporting;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.Rendering;

public static class YoloQuestBuild
{
    public static void Build()
    {
        PlayerSettings.SetApplicationIdentifier(NamedBuildTarget.Android, "com.armusician.yoloprobe");
        PlayerSettings.productName = "AR Musician YOLO Probe";
        PlayerSettings.bundleVersion = "0.1.0";
        PlayerSettings.Android.bundleVersionCode = 1;
        PlayerSettings.SetScriptingBackend(NamedBuildTarget.Android, ScriptingImplementation.IL2CPP);
        PlayerSettings.Android.targetArchitectures = AndroidArchitecture.ARM64;
        PlayerSettings.Android.minSdkVersion = AndroidSdkVersions.AndroidApiLevel32;
        PlayerSettings.Android.targetSdkVersion = AndroidSdkVersions.AndroidApiLevel34;
        PlayerSettings.Android.useCustomKeystore = false;
        PlayerSettings.Android.splitApplicationBinary = false;
        EditorUserBuildSettings.buildAppBundle = false;
        EditorUserBuildSettings.exportAsGoogleAndroidProject = false;
        PlayerSettings.SetUseDefaultGraphicsAPIs(BuildTarget.Android, false);
        PlayerSettings.SetGraphicsAPIs(BuildTarget.Android, new[] { GraphicsDeviceType.Vulkan });
        PlayerSettings.defaultInterfaceOrientation = UIOrientation.LandscapeLeft;

        const string scenePath = "Assets/YoloQuestProbe.unity";
        var scene = EditorSceneManager.NewScene(NewSceneSetup.DefaultGameObjects, NewSceneMode.Single);
        new GameObject("Standalone YOLO Probe").AddComponent<YoloQuestProbe>();
        if (!EditorSceneManager.SaveScene(scene, scenePath))
            throw new IOException("Could not save probe scene");
        AssetDatabase.SaveAssets();
        string apk = Argument("-probeApk");
        Directory.CreateDirectory(Path.GetDirectoryName(apk));
        var report = BuildPipeline.BuildPlayer(new BuildPlayerOptions
        {
            scenes = new[] { scenePath },
            locationPathName = apk,
            target = BuildTarget.Android,
            targetGroup = BuildTargetGroup.Android,
            options = BuildOptions.Development
        });
        if (report.summary.result != BuildResult.Succeeded)
            throw new InvalidOperationException($"Android build {report.summary.result}, {report.summary.totalErrors} errors");
        Debug.Log($"YOLO_PROBE_BUILD_PASS {apk} bytes={new FileInfo(apk).Length}");
    }

    public static void Import()
    {
        string input = Argument("-probeOnnx");
        string output = Argument("-probeSentis");
        const string assetPath = "Assets/ProbeModel.onnx";
        if (File.Exists(output)) throw new IOException("Sentis output already exists: " + output);
        Directory.CreateDirectory(Path.GetDirectoryName(output));
        File.Copy(input, Path.Combine(Path.GetDirectoryName(Application.dataPath), assetPath));
        try
        {
            AssetDatabase.ImportAsset(assetPath, ImportAssetOptions.ForceSynchronousImport | ImportAssetOptions.ForceUpdate);
            var asset = AssetDatabase.LoadAssetAtPath<ModelAsset>(assetPath);
            if (asset == null) throw new IOException("ONNX import did not produce a ModelAsset");
            var model = ModelLoader.Load(asset);
            if (model == null || model.inputs.Count != 1 || model.outputs.Count != 2 || model.layers.Count == 0)
                throw new InvalidDataException("Imported graph does not have one input and two outputs");
            string staging = output + "." + Guid.NewGuid().ToString("N") + ".partial";
            ModelWriter.Save(staging, model);
            File.Move(staging, output);
            Debug.Log($"YOLO_PROBE_IMPORT_PASS {output} bytes={new FileInfo(output).Length} layers={model.layers.Count}");
        }
        finally
        {
            AssetDatabase.DeleteAsset(assetPath);
        }
    }

    private static string Argument(string name)
    {
        var args = Environment.GetCommandLineArgs();
        int index = Array.IndexOf(args, name);
        if (index < 0 || index + 1 >= args.Length || !Path.IsPathFullyQualified(args[index + 1]))
            throw new ArgumentException(name + " requires an absolute path");
        return args[index + 1];
    }
}
