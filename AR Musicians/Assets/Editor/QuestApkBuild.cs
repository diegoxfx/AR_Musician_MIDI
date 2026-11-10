using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Unity.InferenceEngine;
using UnityEditor;
using UnityEditor.Build;
using UnityEditor.Build.Reporting;
using UnityEditor.SceneManagement;
using UnityEditor.XR.Management;
using UnityEngine;
using UnityEngine.XR.OpenXR;

/// <summary>Builds the standalone Quest test APK from an isolated Unity checkout.</summary>
public static class QuestApkBuild
{
    private const string ScenePath = "Assets/Scenes/MainScene.unity";
    private const string PianoModelPath = "Assets/Models/Quest/conv_shuffle-vits16_240x320_merged.onnx";
    private const string BongoModelPath = "Assets/Models/Quest/bongo_detector_240x320_384d_bilinear_kpts_merged.onnx";
    private const string PackageName = "com.armusician.midi";

    // Unity.exe -batchmode -quit -projectPath ".../AR Musicians" -buildTarget Android
    //   -executeMethod QuestApkBuild.Build -apkOutput ".../output/AR-Musician-MIDI.apk"
    // Optional: -androidTargetApi 36 (default 35; install the matching Android SDK platform).
    public static void Build()
    {
        var record = new BuildRecord
        {
            startedUtc = DateTime.UtcNow.ToString("o"),
            unityVersion = Application.unityVersion,
            scene = ScenePath,
            packageName = PackageName,
            architecture = "ARM64",
            scriptingBackend = "IL2CPP",
            development = true,
            signing = "Android debug keystore",
            minApi = 32,
            result = "Preflight"
        };
        string reportPath = null;
        var errors = new List<string>();

        try
        {
            record.apkPath = ReadOutputPath();
            reportPath = record.apkPath + ".build-report.json";
            Directory.CreateDirectory(Path.GetDirectoryName(record.apkPath));
            record.targetApi = ReadTargetApi();
            WriteRecord(reportPath, record, errors);

            Require(EditorUserBuildSettings.activeBuildTarget == BuildTarget.Android,
                "Launch Unity with -buildTarget Android so Android scripts are compiled before this method runs.");
            Require(BuildPipeline.IsBuildTargetSupported(BuildTargetGroup.Android, BuildTarget.Android),
                "This Unity installation has no Android build support.");
            Require(File.Exists(ProjectFile(ScenePath)), "Missing build scene: " + ScenePath);
            Require(File.Exists(ProjectFile(PianoModelPath)), "Missing piano model: " + PianoModelPath);
            Require(File.Exists(ProjectFile(BongoModelPath)), "Missing bongo model: " + BongoModelPath);

            var xr = XRGeneralSettingsPerBuildTarget.XRGeneralSettingsForBuildTarget(BuildTargetGroup.Android);
            Require(xr != null && xr.Manager != null, "Android XR Plug-in Management settings are missing.");
            Require(xr.InitManagerOnStart, "Android XR initialization on startup is disabled.");
            Require(xr.Manager.activeLoaders.Any(loader => loader is OpenXRLoader),
                "The existing Android XR settings do not contain an OpenXR loader; no XR settings were changed.");
            record.xrLoaders = xr.Manager.activeLoaders.Select(loader => loader == null ? "Missing" : loader.GetType().FullName).ToArray();

            var scene = EditorSceneManager.OpenScene(ScenePath, OpenSceneMode.Single);
            // Opening a scene can unload assets held only by managed references.
            // Import both first, then load persistent objects into the open scene.
            AssetDatabase.ImportAsset(PianoModelPath, ImportAssetOptions.ForceSynchronousImport);
            AssetDatabase.ImportAsset(BongoModelPath, ImportAssetOptions.ForceSynchronousImport);
            ModelAsset pianoModel = LoadModel(PianoModelPath);
            ModelAsset bongoModel = LoadModel(BongoModelPath);
            var managers = scene.GetRootGameObjects()
                .SelectMany(root => root.GetComponentsInChildren<ModelManager>(true)).ToArray();
            Require(managers.Length == 2, "Expected exactly two ModelManager components in MainScene; found " + managers.Length + ".");
            var piano = managers.Where(manager => HasAncestor(manager.transform, "Piano")).ToArray();
            var bongo = managers.Where(manager => HasAncestor(manager.transform, "Bongo")).ToArray();
            Require(piano.Length == 1 && bongo.Length == 1 && piano[0] != bongo[0],
                "Could not unambiguously identify the Piano and Bongo ModelManager hierarchies.");

            // Use the imported ModelAsset objects, never hand-written GUIDs or file IDs.
            AssignModel(piano[0], pianoModel);
            AssignModel(bongo[0], bongoModel);
            VerifyModel(piano[0], pianoModel);
            VerifyModel(bongo[0], bongoModel);
            Require(EditorSceneManager.SaveScene(scene), "Could not save validated MainScene model assignments.");
            record.models = new[] { PianoModelPath, BongoModelPath };
            // Validate serialized references, not merely in-memory object equality.
            scene = EditorSceneManager.OpenScene(ScenePath, OpenSceneMode.Single);
            managers = scene.GetRootGameObjects()
                .SelectMany(root => root.GetComponentsInChildren<ModelManager>(true)).ToArray();
            foreach (string modelPath in record.models)
            {
                var expected = LoadModel(modelPath);
                Require(managers.Count(manager => new SerializedObject(manager)
                    .FindProperty("modelAsset").objectReferenceValue == expected) == 1,
                    "Saved scene lost or duplicated its model reference: " + modelPath);
                Require(AssetDatabase.GetDependencies(ScenePath, true).Contains(modelPath),
                    "Model is absent from saved scene dependencies: " + modelPath);
            }
            record.serializedModelsVerified = true;
            Debug.Log("[QuestApkBuild] Saved scene model references and dependencies verified.");

            PlayerSettings.SetApplicationIdentifier(NamedBuildTarget.Android, PackageName);
            PlayerSettings.productName = "AR-Musician MIDI";
            PlayerSettings.bundleVersion = "1.0.1";
            PlayerSettings.Android.bundleVersionCode = 2;
            PlayerSettings.SetScriptingBackend(NamedBuildTarget.Android, ScriptingImplementation.IL2CPP);
            PlayerSettings.Android.targetArchitectures = AndroidArchitecture.ARM64;
            PlayerSettings.Android.minSdkVersion = AndroidSdkVersions.AndroidApiLevel32;
            PlayerSettings.Android.targetSdkVersion = (AndroidSdkVersions)record.targetApi;
            PlayerSettings.Android.useCustomKeystore = false;
            PlayerSettings.Android.splitApplicationBinary = false;
            PlayerSettings.Android.buildApkPerCpuArchitecture = false;
            EditorUserBuildSettings.buildAppBundle = false;
            EditorUserBuildSettings.exportAsGoogleAndroidProject = false;
            AssetDatabase.SaveAssets();

            record.result = "Building";
            WriteRecord(reportPath, record, errors);
            Debug.Log("[QuestApkBuild] Building " + record.apkPath + " (ARM64 IL2CPP, min API 32, target API " + record.targetApi + ", development/debug signed).");
            BuildReport report = BuildPipeline.BuildPlayer(new BuildPlayerOptions
            {
                scenes = new[] { ScenePath },
                locationPathName = record.apkPath,
                target = BuildTarget.Android,
                targetGroup = BuildTargetGroup.Android,
                options = BuildOptions.Development | BuildOptions.AllowDebugging | BuildOptions.DetailedBuildReport
            });
            Require(report != null, "Unity did not return a BuildReport.");
            record.result = report.summary.result.ToString();
            record.buildSeconds = report.summary.totalTime.TotalSeconds;
            record.totalErrors = report.summary.totalErrors;
            record.totalWarnings = report.summary.totalWarnings;
            foreach (var step in report.steps)
            foreach (var message in step.messages)
                if (message.type == LogType.Error || message.type == LogType.Exception || message.type == LogType.Assert)
                    errors.Add(step.name + ": " + message.content);

            Require(report.summary.result == BuildResult.Succeeded,
                "Unity APK build ended with " + report.summary.result + "; see the Unity log and JSON report.");
            Require(File.Exists(record.apkPath) && new FileInfo(record.apkPath).Length > 0,
                "Unity reported success but the requested APK is missing or empty.");
            record.packedModelBytes = record.models.Select(modelPath =>
                report.packedAssets.SelectMany(asset => asset.contents)
                    .Where(info => info.sourceAssetPath == modelPath)
                    .Sum(info => (long)info.packedSize)).ToArray();
            for (int i = 0; i < record.models.Length; i++)
            {
                Require(record.packedModelBytes[i] > 1024 * 1024,
                    "Build report does not contain model weights: " + record.models[i]);
                Debug.Log("[QuestApkBuild] Packed " + record.models[i] + ": " + record.packedModelBytes[i] + " bytes.");
            }
            record.apkBytes = new FileInfo(record.apkPath).Length;
            record.finishedUtc = DateTime.UtcNow.ToString("o");
            WriteRecord(reportPath, record, errors);
            Debug.Log("[QuestApkBuild] SUCCESS: " + record.apkPath + " (" + record.apkBytes + " bytes). Report: " + reportPath);
        }
        catch (Exception exception)
        {
            errors.Add(exception.ToString());
            record.result = "Failed";
            record.finishedUtc = DateTime.UtcNow.ToString("o");
            if (reportPath != null)
            {
                try { WriteRecord(reportPath, record, errors); }
                catch (Exception reportException) { Debug.LogError("[QuestApkBuild] Could not write report: " + reportException); }
            }
            Debug.LogError("[QuestApkBuild] FAILED: " + exception);
            if (Application.isBatchMode)
                EditorApplication.Exit(1);
            throw;
        }
    }

    private static ModelAsset LoadModel(string path)
    {
        var models = AssetDatabase.LoadAllAssetsAtPath(path).OfType<ModelAsset>().ToArray();
        Require(models.Length == 1, "Expected one imported Unity.InferenceEngine.ModelAsset at " + path + "; found " + models.Length + ". Check ONNX importer errors.");
        Require(models[0] != null && EditorUtility.IsPersistent(models[0]) && AssetDatabase.GetAssetPath(models[0]) == path,
            "Imported model is not a live persistent asset: " + path);
        return models[0];
    }

    private static bool HasAncestor(Transform current, string expectedName)
    {
        for (; current != null; current = current.parent)
            if (string.Equals(current.name, expectedName, StringComparison.OrdinalIgnoreCase))
                return true;
        return false;
    }

    private static void AssignModel(ModelManager manager, ModelAsset model)
    {
        Require(model != null && EditorUtility.IsPersistent(model), "Cannot assign a missing or unloaded model.");
        var serialized = new SerializedObject(manager);
        var property = serialized.FindProperty("modelAsset");
        Require(property != null && property.propertyType == SerializedPropertyType.ObjectReference,
            "ModelManager.modelAsset is not the expected serialized object reference.");
        property.objectReferenceValue = model;
        serialized.ApplyModifiedPropertiesWithoutUndo();
        EditorUtility.SetDirty(manager);
    }

    private static void VerifyModel(ModelManager manager, ModelAsset expected)
    {
        Require(expected != null && EditorUtility.IsPersistent(expected), "Expected model was unloaded before verification.");
        Require(new SerializedObject(manager).FindProperty("modelAsset").objectReferenceValue == expected,
            "Model assignment verification failed on " + manager.name + ".");
    }

    private static string ReadOutputPath()
    {
        string value = ReadArgument("-apkOutput");
        Require(!string.IsNullOrWhiteSpace(value) && Path.IsPathRooted(value),
            "Supply -apkOutput with an absolute .apk file path outside Assets and Packages.");
        string path = Path.GetFullPath(value);
        Require(string.Equals(Path.GetExtension(path), ".apk", StringComparison.OrdinalIgnoreCase),
            "-apkOutput must name an .apk file.");
        foreach (string folder in new[] { Application.dataPath, ProjectFile("Packages") })
        {
            string prefix = Path.GetFullPath(folder).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar) + Path.DirectorySeparatorChar;
            Require(!path.StartsWith(prefix, StringComparison.OrdinalIgnoreCase), "APK output must be outside " + folder + ".");
        }
        return path;
    }

    private static string ProjectFile(string relativePath)
    {
        return Path.Combine(Path.GetDirectoryName(Application.dataPath), relativePath);
    }

    private static int ReadTargetApi()
    {
        string value = ReadArgument("-androidTargetApi");
        if (value == null) return 35;
        Require(int.TryParse(value, out int api) && (api == 35 || api == 36),
            "-androidTargetApi must be 35 or 36, matching an installed Android SDK platform.");
        return api;
    }

    private static string ReadArgument(string name)
    {
        string[] arguments = Environment.GetCommandLineArgs();
        int index = Array.FindIndex(arguments, argument => string.Equals(argument, name, StringComparison.OrdinalIgnoreCase));
        if (index < 0) return null;
        Require(index + 1 < arguments.Length && !arguments[index + 1].StartsWith("-", StringComparison.Ordinal), "Missing value for " + name + ".");
        return arguments[index + 1];
    }

    private static void Require(bool condition, string message)
    {
        if (!condition) throw new BuildFailedException(message);
    }

    private static void WriteRecord(string path, BuildRecord record, List<string> errors)
    {
        record.errors = errors.ToArray();
        File.WriteAllText(path, JsonUtility.ToJson(record, true));
    }

    [Serializable]
    private sealed class BuildRecord
    {
        public string startedUtc, finishedUtc, unityVersion, scene, apkPath, packageName;
        public string architecture, scriptingBackend, signing, result;
        public bool development, serializedModelsVerified;
        public int minApi, targetApi;
        public int totalErrors, totalWarnings;
        public long apkBytes;
        public double buildSeconds;
        public string[] xrLoaders, models, errors;
        public long[] packedModelBytes;
    }
}
