using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using UnityEditor;
using UnityEditor.Build;
using UnityEditor.Build.Reporting;
using UnityEditor.SceneManagement;
using UnityEditor.XR.Management;
using UnityEngine;
using UnityEngine.EventSystems;
using UnityEngine.SceneManagement;
using UnityEngine.UI;
using UnityEngine.XR.Management;

public static class DesktopSimulationBuild
{
    private const string ScenePath = "Assets/Scenes/MainScene.unity";

    [MenuItem("AR Musician/Build Windows Simulator")]
    private static void BuildFromMenu()
    {
        string output = EditorUtility.SaveFilePanel("Build Windows Simulator", "", "AR-Musician-Simulator", "exe");
        if (!string.IsNullOrEmpty(output) && EditorSceneManager.SaveCurrentModifiedScenesIfUserWantsTo()) BuildTo(output);
    }

    public static void Run()
    {
        string output = DesktopPianoSimulator.Argument("-simOutput");
        if (string.IsNullOrEmpty(output) || !Path.IsPathRooted(output))
            throw new BuildFailedException("Supply -simOutput with an absolute .exe path.");
        BuildTo(output);
    }

    private static void BuildTo(string output)
    {
        if (EditorUserBuildSettings.activeBuildTarget != BuildTarget.StandaloneWindows64)
            throw new BuildFailedException("Switch to Windows x86_64 before building the desktop simulator (-buildTarget Win64 in batch mode).");
        string path = Path.GetFullPath(output);
        if (!string.Equals(Path.GetExtension(path), ".exe", StringComparison.OrdinalIgnoreCase))
            throw new BuildFailedException("The simulator output must be a Windows .exe.");
        string project = Path.GetDirectoryName(Application.dataPath);
        foreach (string assetFolder in new[] { "Assets", "Packages", "ProjectSettings" })
        {
            string prefix = Path.Combine(project, assetFolder) + Path.DirectorySeparatorChar;
            if (path.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
                throw new BuildFailedException("Place the simulator build outside " + assetFolder + ".");
        }
        Directory.CreateDirectory(Path.GetDirectoryName(path));

        var xr = XRGeneralSettingsPerBuildTarget.XRGeneralSettingsForBuildTarget(BuildTargetGroup.Standalone);
        string xrAsset = xr == null ? null : AssetDatabase.GetAssetPath(xr);
        string xrPath = string.IsNullOrEmpty(xrAsset) ? null : Path.Combine(project, xrAsset);
        byte[] oldXrData = xrPath == null ? null : File.ReadAllBytes(xrPath);
        var debugger = AssetDatabase.LoadMainAssetAtPath("Assets/Resources/ImmersiveDebuggerSettings.asset");
        var debuggerSettings = debugger == null ? null : new SerializedObject(debugger);
        bool oldDebuggerEnabled = debuggerSettings?.FindProperty("immersiveDebuggerEnabled")?.boolValue ?? false;
        var oldBackend = PlayerSettings.GetScriptingBackend(NamedBuildTarget.Standalone);
        var oldFullscreen = PlayerSettings.fullScreenMode;
        string oldName = PlayerSettings.productName;
        var oldPreloadedAssets = PlayerSettings.GetPreloadedAssets();
        var oldScenes = EditorSceneManager.GetSceneManagerSetup();
        string temporaryScene = AssetDatabase.GenerateUniqueAssetPath("Assets/Scenes/DesktopSimulation.unity");
        try
        {
            if (!AssetDatabase.CopyAsset(ScenePath, temporaryScene))
                throw new BuildFailedException("Could not copy the main scene for the simulator.");
            var scene = EditorSceneManager.OpenScene(temporaryScene, OpenSceneMode.Single);
            PrepareScene(scene);
            if (!EditorSceneManager.SaveScene(scene))
                throw new BuildFailedException("Could not save the temporary simulator scene.");

            DisableStandaloneXr(xr);
            SetDebuggerEnabled(debuggerSettings, false);
            if (oldBackend != ScriptingImplementation.Mono2x)
                PlayerSettings.SetScriptingBackend(NamedBuildTarget.Standalone, ScriptingImplementation.Mono2x);
            PlayerSettings.fullScreenMode = FullScreenMode.Windowed;
            PlayerSettings.productName = "AR-Musician Desktop Simulator";
            var report = BuildPipeline.BuildPlayer(new BuildPlayerOptions
            {
                scenes = new[] { temporaryScene },
                locationPathName = path,
                target = BuildTarget.StandaloneWindows64,
                options = BuildOptions.None
            });
            if (report.summary.result != BuildResult.Succeeded || !File.Exists(path))
                throw new BuildFailedException("Desktop simulator build failed: " + report.summary.result);
            Debug.Log("[Desktop simulator] Built " + path);
        }
        finally
        {
            SetDebuggerEnabled(debuggerSettings, oldDebuggerEnabled);
            if (oldBackend != ScriptingImplementation.Mono2x)
                PlayerSettings.SetScriptingBackend(NamedBuildTarget.Standalone, oldBackend);
            PlayerSettings.fullScreenMode = oldFullscreen;
            PlayerSettings.productName = oldName;
            PlayerSettings.SetPreloadedAssets(oldPreloadedAssets);
            AssetDatabase.SaveAssets();
            if (oldXrData != null)
            {
                // Preserve any missing loader references and the Android half
                // of this shared asset as authored. Reimport the restored file.
                File.WriteAllBytes(xrPath, oldXrData);
                AssetDatabase.ImportAsset(xrAsset, ImportAssetOptions.ForceUpdate | ImportAssetOptions.ForceSynchronousImport);
            }
            if (oldScenes.Any(item => !string.IsNullOrEmpty(item.path))) EditorSceneManager.RestoreSceneManagerSetup(oldScenes);
            else EditorSceneManager.NewScene(NewSceneSetup.EmptyScene, NewSceneMode.Single);
            if (AssetDatabase.LoadAssetAtPath<SceneAsset>(temporaryScene) != null) AssetDatabase.DeleteAsset(temporaryScene);
        }
    }

    private static void DisableStandaloneXr(XRGeneralSettings settings)
    {
        if (settings == null) return;
        settings.InitManagerOnStart = false;
        EditorUtility.SetDirty(settings);
        if (settings.Manager != null)
        {
            // XR Management's native boot pre-initialization also uses the
            // first Standalone loader, independently of InitManagerOnStart.
            if (!settings.Manager.TrySetLoaders(new List<XRLoader>()))
                throw new BuildFailedException("Could not disable Standalone XR loaders.");
            EditorUtility.SetDirty(settings.Manager);
        }
        AssetDatabase.SaveAssetIfDirty(settings);
    }

    private static void SetDebuggerEnabled(SerializedObject settings, bool enabled)
    {
        var property = settings?.FindProperty("immersiveDebuggerEnabled");
        if (property == null || property.boolValue == enabled) return;
        property.boolValue = enabled;
        settings.ApplyModifiedPropertiesWithoutUndo();
        AssetDatabase.SaveAssetIfDirty(settings.targetObject);
    }

    private static void PrepareScene(Scene scene)
    {
        var roots = scene.GetRootGameObjects();
        foreach (var transform in roots.SelectMany(root => root.GetComponentsInChildren<Transform>(true)))
        {
            // Deactivate hardware before its Awake; keep the scene's canvas objects,
            // UI style components, collocated gameplay managers and callbacks.
            if (transform.name.StartsWith("[BuildingBlock]", StringComparison.Ordinal) ||
                transform.name == "ISDK_RayCanvasInteraction" || transform.name == "ISDK_PokeCanvasInteraction" ||
                transform.name == "==== RayCast Util ===" || transform.name == "PassthroughCameraAccess" ||
                transform.name == "EnvironmentRayCastManager")
                transform.gameObject.SetActive(false);
        }
        foreach (var root in roots)
        {
            foreach (var manager in root.GetComponentsInChildren<ModelManager>(true))
            {
                manager.enabled = false;
                var serialized = new SerializedObject(manager);
                serialized.FindProperty("modelAsset").objectReferenceValue = null;
                serialized.FindProperty("OnnxModel").objectReferenceValue = null;
                serialized.ApplyModifiedPropertiesWithoutUndo();
            }
            foreach (var behaviour in root.GetComponentsInChildren<Behaviour>(true))
                if (behaviour is CVPlaneFinderGeneric || behaviour is CVCircleFinder || behaviour is CameraViewer ||
                    behaviour is MultiplayerControllerGeneric)
                    behaviour.enabled = false;
        }

        var cameraObject = new GameObject("Simulator Camera", typeof(Camera), typeof(AudioListener));
        SceneManager.MoveGameObjectToScene(cameraObject, scene);
        cameraObject.tag = "MainCamera";
        var camera = cameraObject.GetComponent<Camera>();
        camera.clearFlags = CameraClearFlags.SolidColor;
        camera.backgroundColor = new Color32(29, 33, 45, 255);
        camera.nearClipPlane = 0.03f;
        camera.farClipPlane = 25f;
        camera.stereoTargetEye = StereoTargetEyeMask.None;
        camera.transform.position = new Vector3(0, 1.46f, 0);
        camera.fieldOfView = 49;

        foreach (var canvas in roots.SelectMany(root => root.GetComponentsInChildren<Canvas>(true)))
        {
            if (canvas.renderMode == RenderMode.WorldSpace) canvas.worldCamera = camera;
            if (canvas.GetComponent<GraphicRaycaster>() == null) canvas.gameObject.AddComponent<GraphicRaycaster>();
        }

        var eventSystem = roots.SelectMany(root => root.GetComponentsInChildren<EventSystem>(true)).Single();
        foreach (var module in eventSystem.GetComponents<BaseInputModule>()) module.enabled = false;
        eventSystem.gameObject.AddComponent<StandaloneInputModule>();
        // Space is a simulator transport shortcut and should never activate a
        // previously selected button. Mouse interaction is unchanged.
        eventSystem.sendNavigationEvents = false;

        var simulator = new GameObject("Desktop Piano Simulator", typeof(DesktopPianoSimulator));
        SceneManager.MoveGameObjectToScene(simulator, scene);
    }
}
