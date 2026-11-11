#if UNITY_EDITOR || UNITY_STANDALONE_WIN
using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using ARMusician.Midi;
using Oculus.Interaction.Surfaces;
using TMPro;
using UnityEngine;
using UnityEngine.EventSystems;
using UnityEngine.UI;

/// <summary>Supplies a desktop camera, a practice keyboard and input to the MainScene.</summary>
public sealed class DesktopPianoSimulator : MonoBehaviour
{
    private const int FirstPitch = 36;
    private const int KeyCount = 61;
    private const float Width = 0.93f;
    private const float Surface = 0.8f;
    private const float Back = 1.68f;
    private const float Depth = 0.19f;
    private static readonly KeyCode[] TypingKeys =
    {
        KeyCode.A, KeyCode.W, KeyCode.S, KeyCode.E, KeyCode.D, KeyCode.F, KeyCode.T,
        KeyCode.G, KeyCode.Y, KeyCode.H, KeyCode.U, KeyCode.J, KeyCode.K
    };

    private readonly Dictionary<Collider, int> keyColliders = new Dictionary<Collider, int>();
    private readonly Dictionary<int, Renderer> keyRenderers = new Dictionary<int, Renderer>();
    private readonly Dictionary<int, int> held = new Dictionary<int, int>();
    private readonly Dictionary<KeyCode, int> heldTypingKeys = new Dictionary<KeyCode, int>();
    private readonly Dictionary<BoundsClipper, (Vector3 Position, Vector3 Size)> legacyClipBounds =
        new Dictionary<BoundsClipper, (Vector3 Position, Vector3 Size)>();
    private readonly List<Material> materials = new List<Material>();
    private Camera view;
    private RhythmGameManager game;
    private PianoManagerInstrumentDefiner definer;
    private PianoStrategy piano;
    private MenuControllerGeneric menus;
    private PianoMidiFeedback midi;
    private CountdownUIGeneric countdown;
    private Vector3 countdownOriginalPosition;
    private Vector3 countdownOriginalScale;
    private Vector3 pauseCanvasOriginalPosition;
    private GameObject keyboard;
    private Material white, black, pressed;
    private int typingOctave = 60;
    private int mousePitch = -1;
    private bool playedSong;
    private bool showControls;
    private bool compactMenus;
    private bool runAudit, runVerification, runShutdownVerification;
    private AppLanguage auditStartingLanguage;
    private GUIStyle controlText;
    private string captureDirectory;
    private int captureIndex;

    public static string Argument(string name)
    {
        string[] args = Environment.GetCommandLineArgs();
        int index = Array.IndexOf(args, name);
        return index >= 0 && index + 1 < args.Length ? args[index + 1] : null;
    }

    private void Awake()
    {
        view = Camera.main;
        game = FindFirstObjectByType<RhythmGameManager>();
        definer = FindFirstObjectByType<PianoManagerInstrumentDefiner>();
        piano = FindFirstObjectByType<PianoStrategy>();
        countdown = FindFirstObjectByType<CountdownUIGeneric>();
        if (countdown != null && countdown.countdown != null)
        {
            countdownOriginalPosition = countdown.countdown.transform.position;
            countdownOriginalScale = countdown.countdown.transform.localScale;
        }
        menus = game.menucontroller;
        pauseCanvasOriginalPosition = menus.singleplayerPauseMenu.transform.position;
        ProjectConfig.Settings.enableMultiplayer = false;
        ProjectConfig.Settings.useMultiplayer = false;
        ProjectConfig.Settings.master = true;
        Application.runInBackground = true;
        Application.targetFrameRate = 60;
        captureDirectory = Argument("-simCaptureDir");
        if (string.IsNullOrEmpty(captureDirectory)) captureDirectory = Path.Combine(Application.persistentDataPath, "Captures");
        string[] arguments = Environment.GetCommandLineArgs();
        runAudit = arguments.Contains("-simAudit");
        runShutdownVerification = arguments.Contains("-simVerifyShutdown");
        runVerification = runShutdownVerification || arguments.Contains("-simVerify");
        if (runAudit)
        {
            foreach (var host in new[] { menus.mainMenu, menus.singlePlayerLobbyMenu, menus.songMenu, menus.singleplayerPauseMenu })
                foreach (var clipper in host.GetComponentsInChildren<BoundsClipper>(true))
                    legacyClipBounds[clipper] = (clipper.Position, clipper.Size);
        }
        if (runVerification || arguments.Contains("-simFixture")) DesktopSimulationFixture.Install();
        BuildKeyboard();
    }

    private IEnumerator Start()
    {
        yield return null; // The scene's PianoStrategy attaches the shared MIDI feedback in Start.
        midi = piano.MidiFeedback;
        if (midi == null || !midi.IsInitialized)
        {
            Debug.LogError("[Desktop simulator] The shared MIDI receiver did not initialize.");
            if (runAudit || runVerification) Application.Quit(1);
            yield break;
        }
        // A hidden automation window has no OS focus. The scripted scenario
        // drives the same gameplay state as an active desktop user.
        if (runAudit || runVerification) game.SetApplicationFocused(true);
        compactMenus = FindNamedButton("BrowseSongsButton") != null;
        if (!runAudit || !compactMenus) midi.EnableVirtualInput();
        Debug.Log("[Desktop simulator] Ready. F1 displays controls. Captures: " + captureDirectory);
        if (runAudit)
        {
            auditStartingLanguage = AppText.Language;
            if (compactMenus) yield return AuditCompactMenus();
            else yield return AuditLegacyMenus();
        }
        else if (runVerification) yield return VerifyFixture();
    }

    private void Update()
    {
        if (Input.GetKeyDown(KeyCode.F1)) showControls = !showControls;
        if (Input.GetKeyDown(KeyCode.F12))
            StartCoroutine(Capture("manual-" + DateTime.UtcNow.ToString("yyyyMMdd-HHmmss-fff") + "-" + (++captureIndex).ToString("D2")));
        if (!game.InputAllowed || midi == null) return;

        if (definer.IsDefining)
        {
            if (held.Count > 0) ReleaseKeys();
            if (Input.GetKeyDown(KeyCode.F2)) RegisterVirtualCorners();
            if (Input.GetKeyDown(KeyCode.Return)) definer.ConfirmFinalPlane();
            return;
        }

        if (Input.GetKeyDown(KeyCode.Space)) ToggleTransport();
        if (Input.GetKeyDown(KeyCode.Escape) && game.playing) ToggleTransport();
        if (Input.GetKeyDown(KeyCode.F3)) CalibrateVirtualRange();
        if (Input.GetKeyDown(KeyCode.B))
        {
            ReleaseKeys();
            midi.BeginVirtualCalibration();
        }
        if (Input.GetKeyDown(KeyCode.F5) && (game.HasSession ||
            (playedSong && game.State == RhythmGameManager.PlaybackState.Idle && menus.songMenu.activeInHierarchy)))
        {
            if (game.HasSession) game.Quit();
            menus.HideAllMenus();
            countdown.StartCountdown();
        }
        if (Input.GetKeyDown(KeyCode.LeftBracket) || Input.GetKeyDown(KeyCode.RightBracket))
        {
            foreach (int pitch in heldTypingKeys.Values) PlayKey(pitch, false);
            heldTypingKeys.Clear();
            typingOctave = Mathf.Clamp(typingOctave + (Input.GetKeyDown(KeyCode.RightBracket) ? 12 : -12), FirstPitch, FirstPitch + 48);
        }
        if (!piano.HasCalibration) return;
        for (int i = 0; i < TypingKeys.Length; i++)
        {
            KeyCode key = TypingKeys[i];
            if (Input.GetKeyDown(key) && heldTypingKeys.TryAdd(key, typingOctave + i)) PlayKey(typingOctave + i, true);
            if (Input.GetKeyUp(key) && heldTypingKeys.Remove(key, out int pitch)) PlayKey(pitch, false);
        }
    }

    private void LateUpdate()
    {
        playedSong |= game.playing;
        bool closeView = game.playing || (compactMenus && game.HasSession);
        if (closeView)
        {
            view.transform.SetPositionAndRotation(new Vector3(0, 1.31f, 0.35f), Quaternion.Euler(9, 0, 0));
            view.fieldOfView = 37;
        }
        else if (compactMenus)
        {
            view.transform.SetPositionAndRotation(new Vector3(0, 1.43f, 0.35f), Quaternion.Euler(definer.IsDefining ? 14 : 12, 0, 0));
            view.fieldOfView = 40;
        }
        else
        {
            view.transform.SetPositionAndRotation(new Vector3(0, 1.46f, 0), Quaternion.Euler(definer.IsDefining ? 5 : 0, 0, 0));
            view.fieldOfView = 49;
        }
        if (compactMenus)
        {
            // Position the inactive canvas during playback so its first paused
            // frame is already closer than the notes. The shell preserves its
            // viewport position and size when the canvas depth changes.
            menus.singleplayerPauseMenu.transform.position = game.HasSession
                ? view.ViewportToWorldPoint(new Vector3(0.5f, 0.5f, 0.8f))
                : pauseCanvasOriginalPosition;
        }
        if (compactMenus && countdown != null && countdown.countdown != null && countdown.countdown.activeInHierarchy)
        {
            var ring = countdown.countdown.transform;
            float depth = view.WorldToViewportPoint(countdownOriginalPosition).z;
            ring.SetPositionAndRotation(view.ViewportToWorldPoint(new Vector3(0.5f, 0.58f, depth)), view.transform.rotation);
            ring.localScale = countdownOriginalScale * 0.55f;
        }
        if (Input.GetMouseButtonUp(0) && mousePitch >= 0)
        {
            PlayKey(mousePitch, false);
            mousePitch = -1;
        }
        if (midi == null || !game.InputAllowed || !Input.GetMouseButtonDown(0) ||
            IsOverMenuControl()) return;

        Ray ray = view.ScreenPointToRay(Input.mousePosition);
        if (definer.IsActive)
        {
            var surface = new Plane(Vector3.up, new Vector3(0, Surface, Back));
            if (!surface.Raycast(ray, out float distance)) return;
            Vector3 point = ray.GetPoint(distance);
            if (Mathf.Abs(point.x) <= Width / 2 + 0.025f && point.z >= Back - Depth - 0.025f && point.z <= Back + 0.025f)
            {
                point.x = Mathf.Clamp(point.x, -Width / 2, Width / 2);
                point.z = Mathf.Clamp(point.z, Back - Depth, Back);
                definer.RegisterPoint(point);
            }
        }
        else if (!definer.IsDefining && piano.HasCalibration)
        {
            float closest = float.PositiveInfinity;
            foreach (var key in keyColliders)
                if (key.Key.Raycast(ray, out RaycastHit hit, closest))
                {
                    mousePitch = key.Value;
                    closest = hit.distance;
                }
            if (mousePitch >= 0) PlayKey(mousePitch, true);
        }
    }

    private static bool IsOverMenuControl()
    {
        var events = EventSystem.current;
        if (events == null || !events.IsPointerOverGameObject()) return false;
        var hits = new List<RaycastResult>();
        events.RaycastAll(new PointerEventData(events) { position = Input.mousePosition }, hits);
        // Calibration instructions and menu background images should let a
        // desktop click reach a simulated key. An interactive control owns it.
        return hits.Count > 0 && ExecuteEvents.GetEventHandler<IPointerDownHandler>(hits[0].gameObject) != null;
    }

    private void BuildKeyboard()
    {
        keyboard = new GameObject("Virtual 61-key Piano");
        keyboard.transform.SetParent(transform, false);
        white = MakeMaterial(new Color32(233, 238, 245, 255));
        black = MakeMaterial(new Color32(27, 32, 41, 255));
        pressed = MakeMaterial(new Color32(102, 230, 179, 255));
        var body = MakeMaterial(new Color32(48, 58, 75, 255));
        var caseObject = CreateCube("Piano case", new Vector3(0, Surface - 0.025f, Back - Depth / 2),
            new Vector3(Width + 0.034f, 0.04f, Depth + 0.024f), body);
        caseObject.GetComponent<Collider>().enabled = false;

        foreach (var key in KeyboardLayout.Build(FirstPitch, KeyCount, piano.plane.whiteToBlackRatio))
        {
            bool accidental = MidiNotes.IsBlack(key.Note);
            float length = accidental ? Depth * 0.62f : Depth;
            var item = CreateCube(MidiNotes.Format(key.Note),
                new Vector3(key.Center * Width, Surface + (accidental ? 0.006f : -0.006f), Back - length / 2),
                new Vector3(key.Width * Width * (accidental ? 0.91f : 0.96f), 0.012f, length), accidental ? black : white);
            keyColliders.Add(item.GetComponent<Collider>(), key.Note);
            keyRenderers.Add(key.Note, item.GetComponent<Renderer>());
        }
    }

    private Material MakeMaterial(Color color)
    {
        var material = new Material(piano.noteCubePrefab.GetComponent<Renderer>().sharedMaterial) { color = color };
        materials.Add(material);
        return material;
    }

    private GameObject CreateCube(string name, Vector3 position, Vector3 size, Material material)
    {
        var cube = GameObject.CreatePrimitive(PrimitiveType.Cube);
        cube.name = name;
        cube.transform.SetParent(keyboard.transform, false);
        cube.transform.SetPositionAndRotation(position, Quaternion.identity);
        cube.transform.localScale = size;
        cube.GetComponent<Renderer>().sharedMaterial = material;
        return cube;
    }

    private void RegisterVirtualCorners()
    {
        // F2 restarts a partially captured manual pose before supplying all three points.
        var corners = new[]
        {
            new Vector3(-Width / 2, Surface, Back),
            new Vector3(Width / 2, Surface, Back),
            new Vector3(Width / 2, Surface, Back - Depth)
        };
        if (definer.CanConfirm) return;
        if (!definer.IsActive)
        {
            definer.TryRegisterDetectedPoints(corners);
            return;
        }
        definer.ResetDefinition();
        foreach (var point in corners) definer.RegisterPoint(point);
    }

    private void CalibrateVirtualRange()
    {
        if (!game.IsPiano || !game.CanSelectInstrument || !piano.HasCalibration) return;
        ReleaseKeys();
        midi.BeginVirtualCalibration();
        SendVirtualEndpoints();
    }

    private void SendVirtualEndpoints()
    {
        PlayKey(FirstPitch, true);
        PlayKey(FirstPitch, false);
        PlayKey(FirstPitch + KeyCount - 1, true);
        PlayKey(FirstPitch + KeyCount - 1, false);
    }

    private void PlayKey(int pitch, bool on)
    {
        if (!keyRenderers.TryGetValue(pitch, out var renderer)) return;
        held.TryGetValue(pitch, out int sources);
        if (!on && sources == 0) return;
        int next = sources + (on ? 1 : -1);
        if (next == 0) held.Remove(pitch);
        else held[pitch] = next;
        if ((sources == 0) == (next == 0)) return;
        renderer.sharedMaterial = on ? pressed : MidiNotes.IsBlack(pitch) ? black : white;
        midi.ReceiveVirtualNote(pitch, on);
    }

    private void ReleaseKeys()
    {
        heldTypingKeys.Clear();
        foreach (int pitch in held.Keys.ToArray())
        {
            held[pitch] = 1;
            PlayKey(pitch, false);
        }
        mousePitch = -1;
    }

    private void OnApplicationFocus(bool focused)
    {
        if (!focused && midi != null) ReleaseKeys();
    }

    private void OnDestroy()
    {
        foreach (var material in materials) Destroy(material);
    }

    private void ToggleTransport()
    {
        if (game.playing)
        {
            game.Pause();
            game.RestorePlaybackMenu();
        }
        else if (game.State == RhythmGameManager.PlaybackState.Paused)
        {
            menus.HideAllMenus();
            countdown.ResumeCountdown();
        }
    }

    private void OnGUI()
    {
        if (!showControls) return;
        if (controlText == null) controlText = new GUIStyle(GUI.skin.label) { wordWrap = true };
        GUILayout.BeginArea(new Rect(12, 12, 420, 208), GUI.skin.box);
        GUILayout.Label(AppText.Choose("DESKTOP SIMULATOR   ·   F1 hides controls", "SIMULADOR DE PC   ·   F1 oculta los controles"), controlText);
        GUILayout.Label(AppText.Choose("Click menus and piano keys. A W S E D F T G Y H U J K play a chromatic octave. [ ] shift octaves.",
            "Haz clic en los menús y las teclas. A W S E D F T G Y H U J K tocan una octava cromática. [ ] cambia de octava."), controlText);
        GUILayout.Label(AppText.Choose("Setup: click the back-left, back-right, front-right corners, or use F2. Enter confirms the pose.",
            "Ajuste: haz clic en las esquinas posterior izquierda, posterior derecha y delantera derecha, o usa F2. Intro confirma la posición."), controlText);
        GUILayout.Label(AppText.Choose("B calibrates the two end keys. F3 calibrates the virtual range.",
            "B calibra las dos teclas de los extremos. F3 calibra el rango virtual."), controlText);
        GUILayout.Label(AppText.Choose("Space pauses/resumes. F5 replays. F12 captures the current screen.",
            "Espacio pausa o reanuda. F5 repite. F12 toma una captura."), controlText);
        GUILayout.EndArea();
    }

    private IEnumerator Capture(string name)
    {
        yield return null;
        yield return new WaitForEndOfFrame();
        Directory.CreateDirectory(captureDirectory);
        string path = Path.Combine(captureDirectory, name + ".png");
        if (runAudit || runVerification) RenderCameraPng(view, path, Screen.width, Screen.height);
        else
        {
            var frame = ScreenCapture.CaptureScreenshotAsTexture();
            try { File.WriteAllBytes(path, frame.EncodeToPNG()); }
            finally { Destroy(frame); }
        }
        Debug.Log("[Desktop simulator] Captured " + path);
    }

    private static void RenderCameraPng(Camera camera, string path, int width, int height)
    {
        var target = RenderTexture.GetTemporary(width, height, 24, RenderTextureFormat.ARGB32);
        var previousActive = RenderTexture.active;
        var previousTarget = camera.targetTexture;
        var image = new Texture2D(width, height, TextureFormat.RGB24, false);
        try
        {
            camera.targetTexture = target;
            Canvas.ForceUpdateCanvases();
            camera.Render();
            RenderTexture.active = target;
            image.ReadPixels(new Rect(0, 0, width, height), 0, 0);
            File.WriteAllBytes(path, image.EncodeToPNG());
        }
        finally
        {
            camera.targetTexture = previousTarget;
            RenderTexture.active = previousActive;
            RenderTexture.ReleaseTemporary(target);
            if (Application.isPlaying) Destroy(image); else DestroyImmediate(image);
        }
    }

    private IEnumerator AuditCompactMenus()
    {
        float deadline = Time.realtimeSinceStartup + 15;
        while ((FindNamedButton("PianoSetupButton")?.IsInteractable() != true || SongLoader.CurrentSongs == null || game.IsSongLoading) &&
            Time.realtimeSinceStartup < deadline) yield return null;
        yield return Capture("01-prepare");
        if (!CheckCompactBounds(menus.singlePlayerLobbyMenu, "Prepare")) yield break;
        if (!Check(new[] { "back", "book", "chevron", "minus", "piano", "plus", "settings" }
            .All(name => Resources.Load<Sprite>("PracticeIcons/" + name) != null), "All seven practice icons load as sprites")) yield break;
        if (!ClickNamedButton("PianoSetupButton")) yield break;
        yield return Capture("02-piano-setup");
        if (!ClickNamedButton("ManualSetupButton")) yield break;
        yield return Capture("03-manual-calibration");
        RegisterVirtualCorners();
        if (!Check(definer.CanConfirm, "Three virtual corners are ready to confirm")) yield break;
        yield return new WaitForSecondsRealtime(0.15f);
        yield return Capture("04-pose-confirmation");
        if (!ClickNamedButton("ConfirmPianoButton")) yield break;
        yield return new WaitForSecondsRealtime(0.15f);
        if (!Check(piano.HasCalibration && menus.singlePlayerLobbyMenu.activeInHierarchy,
            "Pose confirmation returns to preparation")) yield break;
        if (!ClickNamedButton("PianoSetupButton")) yield break;
        yield return new WaitForSecondsRealtime(0.15f);
        var calibrationControl = FindNamedButton("CalibrateMidiButton");
        if (!Check(!midi.IsConnected && !midi.RequestCalibration() && calibrationControl != null &&
            !calibrationControl.IsInteractable(), "MIDI calibration stays disabled without a connected device")) yield break;
        midi.EnableVirtualInput();
        yield return new WaitForSecondsRealtime(0.15f);
        if (!ClickNamedButton("CalibrateMidiButton") || !Check(midi.IsCalibrating, "MIDI calibration control starts endpoint capture")) yield break;
        SendVirtualEndpoints();
        if (!Check(midi.IsCalibrated, "Virtual endpoints complete MIDI calibration")) yield break;
        yield return new WaitForSecondsRealtime(0.15f);
        yield return Capture("05-midi-ready");
        if (!ClickNamedButton("SetupBackButton")) yield break;
        yield return new WaitForSecondsRealtime(0.15f);

        if (!ClickNamedButton("BrowseSongsButton")) yield break;
        yield return new WaitForSecondsRealtime(0.15f);
        deadline = Time.realtimeSinceStartup + 15;
        while (FindNamedButton("SongRow1")?.IsInteractable() != true && Time.realtimeSinceStartup < deadline) yield return null;
        yield return Capture("06-library");
        if (!CheckCompactBounds(menus.songMenu, "Library")) yield break;
        var preferred = SongLoader.CurrentSongs?.FirstOrDefault(song => song.title.IndexOf("Carol of the Bells", StringComparison.OrdinalIgnoreCase) >= 0);
        string songButton = "SongRow1";
        if (preferred != null)
        {
            int pageCount = Mathf.CeilToInt(SongLoader.CurrentSongs.Count / 6f);
            for (int page = 0; page < pageCount; page++)
            {
                var match = FindObjectsByType<Button>(FindObjectsSortMode.None).FirstOrDefault(button =>
                    button.name.StartsWith("SongRow", StringComparison.Ordinal) &&
                    button.transform.Find("Title")?.GetComponent<TMP_Text>()?.text == preferred.title);
                if (match != null)
                {
                    songButton = match.name;
                    if (page > 0) yield return Capture("06b-library-carol");
                    break;
                }
                if (FindNamedButton("NextPageButton")?.IsInteractable() != true) break;
                if (!ClickNamedButton("NextPageButton")) yield break;
                yield return new WaitForSecondsRealtime(0.15f);
            }
        }
        if (!ClickNamedButton(songButton)) yield break;
        deadline = Time.realtimeSinceStartup + 15;
        while ((!game.HasLoadedSong || game.IsSongLoading) && Time.realtimeSinceStartup < deadline) yield return null;
        yield return new WaitForSecondsRealtime(0.15f);
        if (!Check(game.HasLoadedSong && menus.singlePlayerLobbyMenu.activeInHierarchy,
            "Song selection loads the chart and returns to preparation")) yield break;
        if (preferred != null && !Check(game.LoadedSong == preferred, "The Carol of the Bells chart is selected")) yield break;
        Debug.Log("[Desktop simulator] Selected chart: " + game.LoadedSong.title);
        if (!ClickNamedButton("RightHandButton") || !Check(game.PracticeHand == HandSelection.Right, "Right-hand control applies to practice")) yield break;
        yield return null;
        if (!ClickNamedButton("LearnModeButton") || !Check(game.LearningMode, "Learn mode control applies to practice")) yield break;
        yield return Capture("07-prepared-right-hand");
        if (!ClickNamedButton("BothHandsButton")) yield break;
        yield return null;
        var referenceLanguage = AppText.Language;
        int referenceSpeed = Mathf.RoundToInt(game.gameSpeed * 100f);
        if (referenceLanguage != AppLanguage.English && !ClickNamedButton("LanguageEnglishButton")) yield break;
        if (!SetAuditSpeed(70)) yield break;
        if (!Check(game.PracticeHand == HandSelection.Both && game.LearningMode && midi.IsCalibrated &&
            AppText.Language == AppLanguage.English && game.State == RhythmGameManager.PlaybackState.Idle,
            "The reference state is ready with both hands, Learn, calibrated MIDI and English")) yield break;
        yield return Capture("ready-reference");
        if (!SetAuditSpeed(referenceSpeed)) yield break;
        if (referenceLanguage == AppLanguage.Spanish && !ClickNamedButton("LanguageSpanishButton")) yield break;
        yield return new WaitForSecondsRealtime(0.15f);
        if (!ClickNamedButton("PlayModeButton")) yield break;
        yield return null;
        if (!ClickNamedButton("SettingsButton")) yield break;

        bool startedSpanish = auditStartingLanguage == AppLanguage.Spanish;
        yield return Capture(startedSpanish ? "08-settings-es" : "08-settings-en");
        if (!ClickNamedButton("SettingsBackButton")) yield break;
        yield return new WaitForSecondsRealtime(0.15f);
        if (!ClickNamedButton(startedSpanish ? "LanguageEnglishButton" : "LanguageSpanishButton")) yield break;
        if (!Check(AppText.Language != auditStartingLanguage, "Language control switches the interface language")) yield break;
        if (!ClickNamedButton("SettingsButton")) yield break;
        yield return Capture(startedSpanish ? "09-settings-en" : "09-settings-es");
        if (!ClickNamedButton("SettingsBackButton")) yield break;
        yield return new WaitForSecondsRealtime(0.15f);
        if (!ClickNamedButton(startedSpanish ? "LanguageSpanishButton" : "LanguageEnglishButton")) yield break;
        yield return new WaitForSecondsRealtime(0.15f);

        if (!ClickNamedButton("StartPracticeButton")) yield break;
        yield return Capture("10-countdown");
        var countdownCorners = new Vector3[4];
        countdown.countdownCircle.rectTransform.GetWorldCorners(countdownCorners);
        if (!Check(countdownCorners.Select(view.WorldToViewportPoint).All(corner =>
            corner.z > 0 && corner.x >= 0.02f && corner.x <= 0.98f && corner.y >= 0.02f && corner.y <= 0.98f),
            "Countdown ring fits in the desktop view")) yield break;
        yield return WaitForPlayback();
        if (!Check(game.playing && PlaybackMenusHidden(), "Playing hides the preparation interface")) yield break;
        yield return new WaitForSeconds(0.75f);
        yield return Capture("11-playing");
        ToggleTransport();
        if (!Check(game.State == RhythmGameManager.PlaybackState.Paused && menus.singleplayerPauseMenu.activeInHierarchy,
            "Pause displays its controls and freezes playback")) yield break;
        yield return Capture("12-pause");
        if (!CheckCompactBounds(menus.singleplayerPauseMenu, "Pause")) yield break;
        if (!ClickNamedButton("ResumePracticeButton")) yield break;
        yield return WaitForPlayback();
        if (!Check(game.playing && PlaybackMenusHidden(), "Resume closes the pause interface")) yield break;
        ToggleTransport();
        yield return new WaitForSecondsRealtime(0.15f);
        if (!ClickNamedButton("RestartPracticeButton")) yield break;
        yield return WaitForPlayback();
        if (!Check(game.playing && PlaybackMenusHidden() && game.SongTime < 0, "Restart starts the same chart from its lead-in")) yield break;
        ToggleTransport();
        yield return new WaitForSecondsRealtime(0.15f);
        if (!ClickNamedButton("ChooseSongButton")) yield break;
        yield return null;
        if (!Check(game.State == RhythmGameManager.PlaybackState.Idle && menus.songMenu.activeInHierarchy &&
            FindObjectsByType<CubeFallBehavior>(FindObjectsSortMode.None).Length == 0, "Choose song returns to the library and clears the waterfall")) yield break;
        yield return Capture("13-return-to-library");
        bool multiplayerVerified = false;
        yield return VerifyMultiplayerTransition(() => multiplayerVerified = true);
        if (!multiplayerVerified) yield break;
        Debug.Log("[Desktop simulator] Compact menu audit passed.");
        FinishScenario(0);
    }

    private bool SetAuditSpeed(int target)
    {
        for (int step = 0; step < 12; step++)
        {
            int current = Mathf.RoundToInt(game.gameSpeed * 100f);
            if (current == target) return true;
            if (!ClickNamedButton(current > target ? "SlowerButton" : "FasterButton")) return false;
        }
        return Check(false, "Practice speed reaches " + target + "%");
    }

    private IEnumerator VerifyMultiplayerTransition(Action completed)
    {
        var shell = FindFirstObjectByType<PracticeOptionsUI>();
        var bundled = SongLoader.CurrentSongs.FirstOrDefault(song => !song.IsExternal);
        if (!Check(shell != null && bundled != null, "A bundled chart is available for multiplayer setup")) yield break;
        shell.SelectSong(bundled);
        float deadline = Time.realtimeSinceStartup + 10;
        while ((game.LoadedSong != bundled || game.IsSongLoading) && Time.realtimeSinceStartup < deadline) yield return null;
        yield return new WaitForSecondsRealtime(0.15f);
        if (!Check(game.LoadedSong == bundled && game.HasLoadedSong, "The bundled chart is ready")) yield break;
        if (!ClickNamedButton("LearnModeButton") || !Check(game.LearningMode, "Learn is enabled before entering multiplayer")) yield break;

        var settings = ProjectConfig.Settings;
        var previous = (settings.enableMultiplayer, settings.useMultiplayer, settings.master);
        try
        {
            // The desktop build keeps MultiplayerControllerGeneric disabled, so
            // its UI can change the mode without opening a Photon connection.
            settings.enableMultiplayer = true;
            if (!ClickNamedButton("SettingsButton")) yield break;
            yield return new WaitForSecondsRealtime(0.15f);
            if (!ClickNamedButton("AdvancedButton")) yield break;
            yield return new WaitForSecondsRealtime(0.15f);
            if (!ClickNamedButton("MultiplayerButton")) yield break;
            yield return new WaitForSecondsRealtime(0.15f);
            if (!Check(settings.useMultiplayer && !game.LearningMode && menus.instrumentSelectorMenu.activeInHierarchy,
                "Entering multiplayer clears Learn and opens instrument selection")) yield break;
            if (!ClickLegacyButton(menus.instrumentSelectorMenu, "selectPiano")) yield break;
            if (!Check(game.IsPiano && game.BeginCountdown(false) >= 0,
                "The bundled chart can begin countdown with the multiplayer piano strategy")) yield break;
            game.CancelCountdown();
            menus.ShowSongMenu();
            yield return new WaitForSecondsRealtime(0.15f);
            if (!Check(menus.songMenu.activeInHierarchy && !shell.IsVisible && legacyClipBounds.Count > 0 &&
                legacyClipBounds.All(entry => entry.Key != null && entry.Key.Position == entry.Value.Position && entry.Key.Size == entry.Value.Size),
                "Returning to the legacy song menu restores the authored clip bounds")) yield break;
        }
        finally
        {
            (settings.enableMultiplayer, settings.useMultiplayer, settings.master) = previous;
        }
        completed();
    }

    private bool CheckCompactBounds(GameObject host, string name)
    {
        var panel = host.transform.Find("Practice" + name + "Panel") as RectTransform;
        var clippers = host.GetComponentsInChildren<BoundsClipper>(true);
        return Check(panel != null && clippers.Length > 0 && clippers.All(clipper =>
        {
            Vector3 position = clipper.transform.InverseTransformPoint(panel.position);
            Vector2 size = panel.rect.size * panel.localScale.x;
            return (clipper.Position - position).sqrMagnitude < 0.01f &&
                ((Vector2)clipper.Size - size).sqrMagnitude < 0.01f;
        }), name + " clip bounds follow the visible compact panel");
    }

    private IEnumerator WaitForPlayback()
    {
        float deadline = Time.realtimeSinceStartup + countdown.countdownTime + 3;
        while (!game.playing && Time.realtimeSinceStartup < deadline) yield return null;
    }

    private static Button FindNamedButton(string name)
    {
        return FindObjectsByType<Button>(FindObjectsSortMode.None).FirstOrDefault(button => button.name == name);
    }

    private bool ClickNamedButton(string name)
    {
        var button = FindNamedButton(name);
        if (!Check(button != null && button.IsInteractable(), "Visible control " + name)) return false;
        var events = EventSystem.current;
        var rect = button.GetComponent<RectTransform>();
        var canvas = button.GetComponentInParent<Canvas>();
        Canvas.ForceUpdateCanvases();
        var pointer = new PointerEventData(events)
        {
            position = RectTransformUtility.WorldToScreenPoint(canvas.renderMode == RenderMode.ScreenSpaceOverlay ? null : view,
                rect.TransformPoint(rect.rect.center)),
            button = PointerEventData.InputButton.Left
        };
        var hits = new List<RaycastResult>();
        events.RaycastAll(pointer, hits);
        var receiver = hits.Count == 0 ? null : ExecuteEvents.GetEventHandler<IPointerClickHandler>(hits[0].gameObject);
        if (receiver != button.gameObject)
            Debug.LogWarning("[Desktop simulator] Pointer for " + name + " at " + pointer.position +
                " hits " + (hits.Count == 0 ? "nothing" : hits[0].gameObject.name) + ", receiver " + (receiver == null ? "none" : receiver.name));
        if (!Check(receiver == button.gameObject, "Pointer reaches " + name)) return false;
        ExecuteEvents.Execute(button.gameObject, pointer, ExecuteEvents.pointerClickHandler);
        return true;
    }

    private IEnumerator AuditLegacyMenus()
    {
        yield return Capture("01-home");
        if (!ClickLegacyButton(menus.mainMenu, "ShowInstrumentDetectorMenu")) yield break;
        yield return Capture("02-instrument-detection");
        var toggle = menus.instrumentDetectorMenu.GetComponentsInChildren<Toggle>().FirstOrDefault();
        if (toggle != null) toggle.isOn = false;
        if (!ClickLegacyButton(menus.instrumentDetectorMenu, "OnPianoPress")) yield break;
        yield return Capture("03-manual-calibration");
        RegisterVirtualCorners();
        yield return Capture("04-pose-confirmation");
        definer.ConfirmFinalPlane();
        CalibrateVirtualRange();
        if (!Check(piano.HasCalibration && midi.IsCalibrated, "Virtual pose and MIDI range calibration")) yield break;

        yield return Capture("05-calibrated-home");
        if (!ClickLegacyButton(menus.mainMenu, "ShowInstrumentSelectorMenu")) yield break;
        yield return Capture("06-instrument-selection");
        if (!ClickLegacyButton(menus.instrumentSelectorMenu, "selectPiano")) yield break;

        float deadline = Time.realtimeSinceStartup + 15;
        while (SongLoader.CurrentSongs == null && Time.realtimeSinceStartup < deadline) yield return null;
        if (!Check(SongLoader.CurrentSongs?.Count > 0, "Bundled song catalog")) yield break;
        yield return Capture("07-song-selection");
        if (!ClickLegacyButton(menus.songMenu, "OnSongSelected")) yield break;
        yield return Capture("08-lobby");
        if (!ClickLegacyButton(menus.singlePlayerLobbyMenu, "StartCountdown")) yield break;
        yield return Capture("09-countdown");

        deadline = Time.realtimeSinceStartup + countdown.countdownTime + 3;
        while (!game.playing && Time.realtimeSinceStartup < deadline) yield return null;
        if (!Check(game.playing && PlaybackMenusHidden(), "Song playback started with menus hidden")) yield break;
        yield return new WaitForSeconds(0.75f);
        yield return Capture("10-waterfall");
        ToggleTransport();
        if (!Check(game.State == RhythmGameManager.PlaybackState.Paused && menus.singleplayerPauseMenu.activeInHierarchy,
            "Transport pause")) yield break;
        yield return Capture("11-pause");
        if (!ClickLegacyButton(menus.singleplayerPauseMenu, "ResumeCountdown")) yield break;
        deadline = Time.realtimeSinceStartup + countdown.countdownTime + 3;
        while (!game.playing && Time.realtimeSinceStartup < deadline) yield return null;
        if (!Check(game.playing && PlaybackMenusHidden(), "Transport resume with menus hidden")) yield break;
        Debug.Log("[Desktop simulator] Audit passed.");
        FinishScenario(0);
    }

    private IEnumerator VerifyFixture()
    {
        float deadline = Time.realtimeSinceStartup + 15;
        while (SongLoader.CurrentSongs == null && Time.realtimeSinceStartup < deadline) yield return null;
        SongData fixture = SongLoader.CurrentSongs?.FirstOrDefault(song => song.IsExternal && song.title == DesktopSimulationFixture.Title);
        if (!Check(fixture != null, "Repeated-note fixture imported through the real song catalog")) yield break;
        if (!Check(!SongLoader.CurrentSongs.Any(song => string.Equals(Path.GetFileName(song.PartialSongData.JsonPath),
            "RuntimeActionBindings.json", StringComparison.OrdinalIgnoreCase)), "OpenXR runtime configuration is excluded from the song catalog")) yield break;

        menus.StartPianoSetup();
        RegisterVirtualCorners();
        definer.ConfirmFinalPlane();
        CalibrateVirtualRange();
        if (!Check(piano.HasCalibration && midi.IsCalibrated && midi.FirstNote == FirstPitch && midi.LastNote == FirstPitch + KeyCount - 1,
            "Virtual pose and endpoint calibration")) yield break;

        // The user can enter setup while holding a typed note. The next
        // press must reach MIDI calibration even if setup consumed its key-up.
        PlayKey(FirstPitch, true);
        menus.StartPianoSetup();
        yield return null;
        if (!Check(!held.ContainsKey(FirstPitch) && keyRenderers[FirstPitch].sharedMaterial == white,
            "Entering setup releases the simulated key and clears its highlight")) yield break;
        RegisterVirtualCorners();
        definer.ConfirmFinalPlane();
        midi.BeginVirtualCalibration();
        SendVirtualEndpoints();
        if (!Check(midi.IsCalibrated && midi.FirstNote == FirstPitch && midi.LastNote == FirstPitch + KeyCount - 1,
            "The re-pressed key reaches manual endpoint calibration after setup")) yield break;

        yield return game.updateSongRoutine(fixture);
        if (!Check(game.SetLearningMode(true), "Learning accepts the simulated MIDI range")) yield break;
        menus.HideAllMenus();
        countdown.StartCountdown();
        deadline = Time.realtimeSinceStartup + countdown.countdownTime + 3;
        while (!game.playing && Time.realtimeSinceStartup < deadline) yield return null;
        if (!Check(game.playing && PlaybackMenusHidden(), "Countdown starts playback with menus hidden")) yield break;
        yield return new WaitForSeconds(0.7f);
        if (runShutdownVerification)
        {
            if (!Check(game.LearningMode && game.playing && FindAnyObjectByType<CubeFallBehavior>() != null,
                "Application shutdown starts with learning active and falling notes present")) yield break;
            // Exercise Unity's real lifecycle without stopping the session first.
            Application.Quit();
            yield break;
        }
        var repeated = FindObjectsByType<CubeFallBehavior>(FindObjectsSortMode.None)
            .Where(cube => cube.keyIndex == 67).Select(cube => cube.GetComponent<Renderer>().bounds)
            .OrderBy(bounds => bounds.center.y).ToArray();
        bool separated = repeated.Length == 3;
        for (int i = 1; i < repeated.Length; i++) separated &= repeated[i - 1].max.y < repeated[i].min.y;
        yield return Capture("fixture-repeated-notes");
        if (!Check(separated, "Three G4 bars have visible space between them (count: " + repeated.Length + ")")) yield break;

        deadline = Time.realtimeSinceStartup + piano.ApproachTime + 3;
        while (!game.WaitingForLearningNotes && Time.realtimeSinceStartup < deadline) yield return null;
        if (!Check(game.WaitingForLearningNotes && game.LearningCompletedGroups == 0, "Learning waits for the initial chord")) yield break;
        PlayKey(48, true);
        PlayKey(67, true);
        PlayKey(48, false);
        if (!Check(game.LearningCompletedGroups == 1, "Virtual MIDI completes the initial chord")) yield break;
        yield return new WaitForSecondsRealtime(0.5f);
        if (!Check(game.WaitingForLearningNotes && game.LearningCompletedGroups == 1, "Held G4 cannot satisfy its repeated strike")) yield break;
        PlayKey(67, false);
        PlayKey(67, true);
        PlayKey(67, false);
        if (!Check(game.LearningCompletedGroups == 2, "G4 release and re-press advances the repeated strike")) yield break;

        var pauseCameraPosition = view.transform.position;
        var pauseCameraRotation = view.transform.rotation;
        float pauseCameraFov = view.fieldOfView;
        ToggleTransport();
        double pauseTime = game.SongTime;
        var pausedBars = FindObjectsByType<CubeFallBehavior>(FindObjectsSortMode.None)
            .Select(cube => (Transform: cube.transform, Position: cube.transform.position)).ToArray();
        yield return new WaitForSecondsRealtime(0.25f);
        bool stableCamera = !compactMenus || (view.transform.position == pauseCameraPosition &&
            view.transform.rotation == pauseCameraRotation && view.fieldOfView == pauseCameraFov);
        if (!Check(game.State == RhythmGameManager.PlaybackState.Paused && menus.singleplayerPauseMenu.activeInHierarchy &&
            Math.Abs(game.SongTime - pauseTime) < 0.001 && pausedBars.Length > 0 &&
            pausedBars.All(bar => bar.Transform != null && bar.Transform.position == bar.Position) && stableCamera,
            "Pause menu is visible while the clock, falling notes and camera stay fixed")) yield break;
        if (compactMenus)
        {
            var pausePanel = menus.singleplayerPauseMenu.transform.Find("PracticePausePanel");
            if (!Check(pausePanel != null && pausedBars.All(bar => view.WorldToViewportPoint(pausePanel.position).z <
                view.WorldToViewportPoint(bar.Position).z), "Pause controls are drawn in front of the frozen notes")) yield break;
        }
        yield return Capture("fixture-paused");
        ToggleTransport();
        deadline = Time.realtimeSinceStartup + countdown.countdownTime + 3;
        while (!game.playing && Time.realtimeSinceStartup < deadline) yield return null;
        if (!Check(game.playing && PlaybackMenusHidden(), "Resume hides the pause menu")) yield break;
        ToggleTransport();
        game.Quit();
        yield return null;
        if (!Check(game.State == RhythmGameManager.PlaybackState.Idle && menus.songMenu.activeInHierarchy &&
            FindObjectsByType<CubeFallBehavior>(FindObjectsSortMode.None).Length == 0,
            "Quitting while paused clears all falling notes and returns to songs")) yield break;
        yield return Capture("fixture-returned-to-songs");
        menus.HideAllMenus();
        countdown.StartCountdown();
        deadline = Time.realtimeSinceStartup + countdown.countdownTime + 3;
        while (!game.playing && Time.realtimeSinceStartup < deadline) yield return null;
        if (!Check(game.playing && game.LearningCompletedGroups == 0, "Replay resets practice progress")) yield break;

        game.Quit();
        if (!Check(game.SetSplitPitch(84) && game.SetHandSelection(HandSelection.Left), "Left-hand mode accepts the practice fixture")) yield break;
        menus.HideAllMenus();
        game.Play();
        yield return null;
        var visibleLeft = FindObjectsByType<CubeFallBehavior>(FindObjectsSortMode.None);
        if (!Check(game.playing && game.LearningTotalGroups == 2 &&
            visibleLeft.Length > 0 && visibleLeft.All(cube => cube.keyIndex == 48 || cube.keyIndex == 55),
            "Left practice displays only bass notes from the labeled left track")) yield break;
        game.Quit();
        if (!Check(game.SetHandSelection(HandSelection.Right), "Right-hand mode accepts the practice fixture")) yield break;
        menus.HideAllMenus();
        game.Play();
        yield return null;
        var visibleRight = FindObjectsByType<CubeFallBehavior>(FindObjectsSortMode.None);
        if (!Check(game.playing && game.LearningTotalGroups == 6 &&
            visibleRight.Length > 0 && visibleRight.All(cube => cube.keyIndex >= 64),
            "Labeled right-hand notes remain right-hand below the chosen split")) yield break;
        Debug.Log("[Desktop simulator] Fixture verification passed.");
        FinishScenario(0);
    }

    private bool PlaybackMenusHidden()
    {
        return !menus.mainMenu.activeInHierarchy && !menus.songMenu.activeInHierarchy &&
            !menus.instrumentSelectorMenu.activeInHierarchy && !menus.instrumentDetectorMenu.activeInHierarchy &&
            !menus.singlePlayerLobbyMenu.activeInHierarchy && !menus.singleplayerPauseMenu.activeInHierarchy &&
            !countdown.countdown.activeInHierarchy;
    }

    private bool ClickLegacyButton(GameObject menu, string method)
    {
        // Persistent callbacks identify original scene buttons independently of copy or labels.
        var button = menu.GetComponentsInChildren<Button>().FirstOrDefault(candidate =>
            Enumerable.Range(0, candidate.onClick.GetPersistentEventCount())
                .Any(i => candidate.onClick.GetPersistentMethodName(i) == method && candidate.onClick.GetPersistentTarget(i) != null));
        if (!Check(button != null && button.IsInteractable(), "Visible button for " + method)) return false;
        button.onClick.Invoke();
        return true;
    }

    private bool Check(bool passed, string condition)
    {
        if (passed) { Debug.Log("[Desktop simulator] Verified: " + condition); return true; }
        Debug.LogError("[Desktop simulator] Audit failed: " + condition);
        FinishScenario(1);
        return false;
    }

    private void FinishScenario(int exitCode)
    {
        if (game.HasSession) game.Quit();
        if (game.LearningMode) game.SetLearningMode(false);
        if (runAudit && AppText.Language != auditStartingLanguage)
        {
            var shell = FindFirstObjectByType<PracticeOptionsUI>();
            if (shell != null) shell.SetLanguage(auditStartingLanguage);
            else AppText.SetLanguage(auditStartingLanguage);
        }
        Application.Quit(exitCode);
    }
}
#endif
