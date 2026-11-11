using System;
using System.Collections.Generic;
using ARMusician.Midi;
using Assets.Scripts.Songs;
using TMPro;
using UnityEngine;
using UnityEngine.Events;
using UnityEngine.UI;

public sealed partial class PracticeOptionsUI
{
    private const float PanelWidth = 420;
    private const float FullHeight = 710;
    private static readonly Color Surface = new Color32(26, 26, 28, 255);
    private static readonly Color Control = new Color32(50, 50, 53, 255);
    private static readonly Color Raised = new Color32(59, 59, 62, 255);
    private static readonly Color Violet = new Color32(101, 56, 244, 255);
    private static readonly Color TextColor = new Color32(247, 246, 248, 255);
    private static readonly Color Muted = new Color32(188, 186, 193, 255);
    private static readonly Color Divider = new Color32(112, 110, 115, 255);
    private static readonly Color Green = new Color32(20, 219, 102, 255);
    private static readonly Color Amber = new Color32(255, 190, 102, 255);

    private sealed class Panel
    {
        public readonly CanvasHost Host;
        public readonly RectTransform Rect;
        public Panel(CanvasHost host, RectTransform rect) { Host = host; Rect = rect; }
    }

    private sealed class Copy
    {
        private readonly TMP_Text label;
        private readonly string english, spanish;
        public Copy(TMP_Text label, string english, string spanish)
        {
            this.label = label;
            this.english = english;
            this.spanish = spanish;
        }
        public void Refresh() => SetText(label, T(english, spanish));
    }

    private sealed class SongRow
    {
        public readonly Button Button;
        public readonly TMP_Text Title, Metadata;
        public SongRow(Button button, TMP_Text title, TMP_Text metadata)
        {
            Button = button;
            Title = title;
            Metadata = metadata;
        }
    }

    private readonly List<Copy> localizedLabels = new List<Copy>();
    private readonly Dictionary<int, Sprite> roundedSprites = new Dictionary<int, Sprite>();
    private readonly Dictionary<string, Sprite> icons = new Dictionary<string, Sprite>();
    private readonly SongRow[] songRows = new SongRow[SongsPerPage];
    private TMP_FontAsset font;
    private Button english, spanish, browse, leftHand, bothHands, rightHand, learn, play, slower, faster, start;
    private Button previousPage, nextPage, setup, settings;
    private Button beginAutomatic, beginManual, capture, resetPoints, confirm, cancelStep, calibrate, reverse;
    private Button guideSound, keyFeedback, splitDown, splitUp;
    private Button resume, restart, chooseSong;
    private Button usePiano, useBongos, startBongoSetup, multiplayer;
    private Image statusDot;
    private TMP_Text songTitle, songMetadata, speed, practiceStatus, guideSummary, startHint;
    private TMP_Text libraryCount, libraryStatus, libraryEmpty, pageNumber;
    private TMP_Text setupState, setupInstruction, setupStatus, midiSetupStatus;
    private TMP_Text guideState, feedbackState, splitValue, pauseSong, pauseStatus, multiplayerHint;
    private RectTransform setupChoices, setupCapture;

    private static string T(string english, string spanish) => AppText.Choose(english, spanish);

    private void BuildPanels()
    {
        var legacyLabel = menus.singlePlayerLobbyMenu.GetComponentInChildren<TMP_Text>(true);
        font = legacyLabel == null ? TMP_Settings.defaultFontAsset : legacyLabel.font;
        BuildPrepare(CreatePanel(PracticeView.Prepare, menus.singlePlayerLobbyMenu, FullHeight));
        BuildLibrary(CreatePanel(PracticeView.Library, menus.songMenu, FullHeight));
        BuildSetup(CreatePanel(PracticeView.PianoSetup, menus.mainMenu, 650));
        BuildSettings(CreatePanel(PracticeView.Settings, menus.mainMenu, 595));
        BuildPause(CreatePanel(PracticeView.Pause, menus.singleplayerPauseMenu, 363));
        BuildAdvanced(CreatePanel(PracticeView.Advanced, menus.mainMenu, 465));
    }

    private RectTransform CreatePanel(PracticeView view, GameObject root, float height)
    {
        if (!hosts.TryGetValue(root, out var host)) hosts.Add(root, host = new CanvasHost(root));
        var rect = Child(root.transform, "Practice" + view + "Panel", 0, 0, PanelWidth, height);
        rect.gameObject.SetActive(false);
        rect.anchorMin = rect.anchorMax = rect.pivot = new Vector2(0.5f, 0.5f);
        Rounded(rect, Surface, 27);
        panels.Add(view, new Panel(host, rect));
        return rect;
    }

    private void PositionPanel(Panel panel)
    {
        const float questScale = 0.78f;
        var rect = panel.Rect;
        rect.localScale = Vector3.one * questScale;
        rect.localRotation = Quaternion.identity;
        rect.localPosition = new Vector3(-350, 336 - rect.rect.height * questScale / 2f, 0);
#if UNITY_EDITOR || UNITY_STANDALONE_WIN
        if (Application.platform == RuntimePlatform.WindowsPlayer || Application.isEditor)
        {
            var camera = Camera.main;
            if (camera != null && !camera.stereoEnabled && camera.pixelHeight > 0 && camera.pixelWidth > 0)
            {
                float height = camera.pixelHeight;
                float width = camera.pixelWidth;
                float scale = Mathf.Min(height * 0.693f / FullHeight, width * 0.46f / PanelWidth);
                float depth = camera.WorldToViewportPoint(panel.Host.Root.transform.position).z;
                var bottom = camera.ViewportToWorldPoint(new Vector3(0, 0, depth));
                var top = camera.ViewportToWorldPoint(new Vector3(0, 1, depth));
                float worldPerPixel = (top - bottom).magnitude / height;
                rect.localScale = Vector3.one * (worldPerPixel * scale / panel.Host.Root.transform.lossyScale.y);
                rect.rotation = camera.transform.rotation;
                rect.position = camera.ViewportToWorldPoint(new Vector3(
                    0.020f + rect.rect.width * scale / (2 * width),
                    0.974f - rect.rect.height * scale / (2 * height), depth));
            }
        }
#endif
        panel.Host.SetBounds(rect.localPosition, rect.rect.size * rect.localScale.x);
    }

    private void BuildPrepare(RectTransform panel)
    {
        Label(panel, "AppName", "AR-Musician", 29, 14, 237, 48, 32, bold: true);
        english = ButtonWithLabel(panel, "LanguageEnglishButton", "EN", 273, 19, 56, 39, 22,
            () => SetLanguage(AppLanguage.English));
        Line(panel, 341, 28, 1, 21);
        spanish = ButtonWithLabel(panel, "LanguageSpanishButton", "ES", 348, 19, 48, 39, 22,
            () => SetLanguage(AppLanguage.Spanish));
        Line(panel, 29, 70, 363, 1);
        songTitle = Label(panel, "SongTitle", "", 29, 79, 366, 43, 34, bold: true, minimum: 23);
        songMetadata = Label(panel, "SongMetadata", "", 29, 123, 364, 31, 22, Muted);
        browse = MakeButton(panel, "BrowseSongsButton", 25, 161, 372, 48, ShowLibrary, Raised, 15);
        AddIcon(browse.transform, "book", "□", 81, 10, 29);
        CopyLabel(browse.transform, "BrowseSongsLabel", "Browse songs", "Ver canciones",
            113, 0, 166, 48, 22, TextAlignmentOptions.Center, true);
        AddIcon(browse.transform, "chevron", "›", 282, 11, 26);
        Line(panel, 29, 223, 363, 1);

        CopyLabel(panel, "HandsLabel", "Hands", "Manos", 29, 234, 103, 45, 24, bold: true);
        leftHand = LocalButton(panel, "LeftHandButton", "Left", "Izq.", 136, 235, 82, 44, 21,
            () => SelectHand(HandSelection.Left));
        bothHands = LocalButton(panel, "BothHandsButton", "Both", "Ambas", 222, 235, 85, 44, 21,
            () => SelectHand(HandSelection.Both));
        rightHand = LocalButton(panel, "RightHandButton", "Right", "Der.", 312, 235, 84, 44, 21,
            () => SelectHand(HandSelection.Right));
        Line(panel, 29, 290, 363, 1);

        CopyLabel(panel, "ModeLabel", "Mode", "Modo", 29, 298, 105, 45, 24, bold: true);
        learn = LocalButton(panel, "LearnModeButton", "Learn", "Aprender", 139, 299, 127, 44, 22,
            () => SelectLearning(true));
        play = LocalButton(panel, "PlayModeButton", "Play", "Tocar", 271, 299, 125, 44, 22,
            () => SelectLearning(false));
        Line(panel, 29, 354, 363, 1);

        CopyLabel(panel, "SpeedLabel", "Speed", "Velocidad", 29, 362, 123, 45, 24, bold: true);
        slower = IconButton(panel, "SlowerButton", "minus", "−", 159, 363, 51, 43, () => AdjustSpeed(-10));
        speed = Label(panel, "SpeedValue", "", 215, 362, 120, 45, 28, bold: true,
            alignment: TextAlignmentOptions.Center);
        faster = IconButton(panel, "FasterButton", "plus", "+", 341, 363, 51, 43, () => AdjustSpeed(10));
        Line(panel, 29, 418, 363, 1);

        statusDot = Rounded(Child(panel, "StatusDot", 36, 435, 19, 19), Amber, 10);
        statusDot.type = Image.Type.Simple;
        practiceStatus = Label(panel, "PracticeStatus", "", 74, 423, 317, 43, 22, bold: true, minimum: 16);
        Line(panel, 29, 472, 363, 1);

        setup = NavigationButton(panel, "PianoSetupButton", "Piano setup", "Ajustar piano", "piano", "▥",
            485, ShowPianoSetup);
        settings = NavigationButton(panel, "SettingsButton", "Settings", "Ajustes", "settings", "⚙",
            545, ShowSettings, 135);
        guideSummary = Label(settings.transform, "GuideSummary", "", 192, 0, 143, 48, 17, Muted,
            alignment: TextAlignmentOptions.Right, minimum: 14);
        startHint = Label(panel, "StartHint", "", 27, 594, 369, 24, 16, Muted, minimum: 12,
            alignment: TextAlignmentOptions.Center);
        start = LocalButton(panel, "StartPracticeButton", "Start practice", "Empezar práctica",
            26, 618, 374, 68, 35, StartPractice, Violet, 18);
    }

    private void BuildLibrary(RectTransform panel)
    {
        Header(panel, "LibraryBackButton", "Song library", "Canciones", ShowPrepare);
        libraryCount = Label(panel, "LibraryCount", "", 28, 79, 367, 42, 21, Muted);
        libraryEmpty = Label(panel, "LibraryEmpty", "", 28, 133, 365, 89, 23, Muted, wrap: true);
        for (int i = 0; i < songRows.Length; i++)
        {
            int row = i;
            var button = MakeButton(panel, "SongRow" + (i + 1), 26, 130 + i * 73, 368, 64,
                () => SelectLibraryRow(row), Control);
            var title = Label(button.transform, "Title", "", 15, 5, 324, 31, 23, bold: true, minimum: 18);
            var metadata = Label(button.transform, "Metadata", "", 15, 35, 324, 23, 17, Muted, minimum: 13);
            AddIcon(button.transform, "chevron", "›", 337, 18, 26);
            songRows[i] = new SongRow(button, title, metadata);
        }
        previousPage = LocalButton(panel, "PreviousPageButton", "Previous", "Anterior", 26, 575, 122, 46, 19,
            () => ChangeLibraryPage(-1));
        pageNumber = Label(panel, "PageNumber", "", 150, 575, 120, 46, 19, Muted, alignment: TextAlignmentOptions.Center);
        nextPage = LocalButton(panel, "NextPageButton", "Next", "Siguiente", 272, 575, 122, 46, 19,
            () => ChangeLibraryPage(1));
        libraryStatus = Label(panel, "LibraryStatus", "", 28, 638, 365, 50, 17, Amber, wrap: true, minimum: 14);
    }

    private void BuildSetup(RectTransform panel)
    {
        Header(panel, "SetupBackButton", "Piano setup", "Ajustar piano", ShowPrepare);
        setupChoices = Child(panel, "SetupChoices", 0, 0, PanelWidth, 650);
        setupState = Label(setupChoices, "PianoSetupStatus", "", 28, 84, 365, 33, 25, bold: true);
        CopyLabel(setupChoices, "PoseInstructions", "Align the note guide with your keyboard.",
            "Alinea la guía de notas con tu teclado.", 28, 123, 366, 52, 20, tint: Muted, wrap: true);
        beginAutomatic = LocalButton(setupChoices, "AutomaticSetupButton", "Automatic capture", "Captura automática",
            26, 186, 368, 51, 24, BeginAutomaticSetup, Violet);
        beginManual = LocalButton(setupChoices, "ManualSetupButton", "Manual calibration", "Calibración manual",
            26, 247, 368, 51, 24, BeginManualSetup);
        Line(setupChoices, 29, 329, 363, 1);
        CopyLabel(setupChoices, "MidiTitle", "MIDI calibration", "Calibración MIDI", 28, 348, 365, 36, 25, bold: true);
        midiSetupStatus = Label(setupChoices, "MidiSetupStatus", "", 28, 394, 365, 85, 21, Muted, wrap: true, minimum: 17);
        calibrate = LocalButton(setupChoices, "CalibrateMidiButton", "Calibrate MIDI", "Calibrar MIDI",
            26, 485, 368, 50, 23, RequestMidiCalibration);
        reverse = LocalButton(setupChoices, "ReverseKeyboardButton", "Reverse key alignment", "Invertir alineación",
            26, 548, 368, 50, 23, ReverseKeyboard);

        setupCapture = Child(panel, "SetupCapture", 0, 0, PanelWidth, 650);
        setupInstruction = Label(setupCapture, "CaptureInstruction", "", 29, 87, 364, 135, 24, bold: true,
            alignment: TextAlignmentOptions.TopLeft, wrap: true);
        setupStatus = Label(setupCapture, "CaptureStatus", "", 29, 238, 364, 132, 21, Muted,
            alignment: TextAlignmentOptions.TopLeft, wrap: true, minimum: 17);
        capture = LocalButton(setupCapture, "CaptureKeyboardButton", "Capture keyboard", "Capturar teclado",
            26, 394, 368, 52, 23, CaptureKeyboard);
        resetPoints = LocalButton(setupCapture, "ResetCornersButton", "Reset corners", "Borrar esquinas",
            26, 394, 368, 52, 23, ResetManualPoints);
        confirm = LocalButton(setupCapture, "ConfirmPianoButton", "Confirm position", "Confirmar posición",
            26, 457, 368, 56, 24, ConfirmPianoPose, Violet);
        cancelStep = LocalButton(setupCapture, "CancelSetupButton", "Cancel", "Cancelar",
            26, 526, 368, 51, 23, ShowPianoSetup);
    }

    private void BuildSettings(RectTransform panel)
    {
        Header(panel, "SettingsBackButton", "Settings", "Ajustes", ShowPrepare);
        guideSound = NavigationButton(panel, "GuideSoundButton", "Guide sound", "Sonido de guía",
            null, null, 100, ToggleGuide, 220, chevron: false);
        guideState = Label(guideSound.transform, "State", "", 240, 0, 112, 48, 19, Muted,
            alignment: TextAlignmentOptions.Right);
        keyFeedback = NavigationButton(panel, "KeyFeedbackButton", "Key feedback", "Indicadores MIDI",
            null, null, 164, ToggleKeyFeedback, 226, chevron: false);
        feedbackState = Label(keyFeedback.transform, "State", "", 248, 0, 104, 48, 19, Muted,
            alignment: TextAlignmentOptions.Right);
        Line(panel, 29, 234, 363, 1);
        CopyLabel(panel, "SplitPitchLabel", "Hand split pitch", "Nota de separación",
            28, 253, 366, 38, 25, bold: true);
        CopyLabel(panel, "SplitPitchExplanation",
            "When a song has no hand labels, the split note and higher notes go to the right hand.",
            "Si la canción no indica las manos, la nota de separación y las más agudas van a la derecha.",
            28, 303, 367, 89, 20, tint: Muted, wrap: true);
        splitDown = IconButton(panel, "SplitDownButton", "minus", "−", 27, 406, 56, 46, () => AdjustSplit(-1));
        splitValue = Label(panel, "SplitPitchValue", "", 86, 405, 248, 48, 24, bold: true,
            alignment: TextAlignmentOptions.Center);
        splitUp = IconButton(panel, "SplitUpButton", "plus", "+", 337, 406, 56, 46, () => AdjustSplit(1));
        Line(panel, 29, 482, 363, 1);
        NavigationButton(panel, "AdvancedButton", "Instruments & multiplayer", "Instrumentos y multijugador",
            null, null, 509, ShowAdvanced, 325);
    }

    private void BuildPause(RectTransform panel)
    {
        CopyLabel(panel, "PauseTitle", "Paused", "En pausa", 28, 13, 363, 52, 32, bold: true);
        Line(panel, 29, 72, 363, 1);
        pauseSong = Label(panel, "PausedSong", "", 29, 84, 361, 36, 23, Muted, minimum: 17);
        resume = LocalButton(panel, "ResumePracticeButton", "Resume", "Reanudar", 26, 137, 368, 53, 25, ResumePractice, Violet);
        restart = LocalButton(panel, "RestartPracticeButton", "Restart", "Reiniciar", 26, 201, 368, 48, 23, RestartPractice);
        chooseSong = LocalButton(panel, "ChooseSongButton", "Choose song", "Elegir canción", 26, 260, 368, 48, 23, ChooseSong);
        pauseStatus = Label(panel, "PauseStatus", "", 27, 319, 366, 32, 17, Amber, minimum: 13,
            alignment: TextAlignmentOptions.Center);
    }

    private void BuildAdvanced(RectTransform panel)
    {
        Header(panel, "AdvancedBackButton", "Instruments & modes", "Instrumentos y modos", ShowSettings);
        usePiano = LocalButton(panel, "UsePianoButton", "Use piano", "Usar piano",
            26, 100, 368, 52, 23, UsePiano);
        useBongos = LocalButton(panel, "UseBongosButton", "Use bongos", "Usar bongós",
            26, 164, 368, 52, 23, UseBongos);
        startBongoSetup = LocalButton(panel, "BongoSetupButton", "Set up bongos", "Ajustar bongós",
            26, 228, 368, 52, 23, StartBongoSetup);
        Line(panel, 29, 303, 363, 1);
        multiplayer = LocalButton(panel, "MultiplayerButton", "Multiplayer", "Multijugador",
            26, 326, 368, 52, 23, EnterMultiplayer);
        multiplayerHint = Label(panel, "MultiplayerHint", "", 29, 389, 363, 55, 18, Muted, wrap: true);
    }

    private void RefreshView()
    {
        switch (CurrentView)
        {
            case PracticeView.Prepare: RefreshPrepare(); break;
            case PracticeView.Library: RefreshLibrary(); break;
            case PracticeView.PianoSetup: RefreshSetup(); break;
            case PracticeView.Settings: RefreshSettings(); break;
            case PracticeView.Pause: RefreshPause(); break;
            case PracticeView.Advanced: RefreshAdvanced(); break;
        }
    }

    private void RefreshPrepare()
    {
        bool canChange = CanNavigate;
        bool pianoReady = game.IsPiano && menus.PianoReady;
        bool midiReady = MidiReady;
        bool spanishSelected = AppText.Language == AppLanguage.Spanish;
        SetButton(english, true, !spanishSelected ? Control : Surface);
        SetButton(spanish, true, spanishSelected ? Control : Surface);

        SetText(songTitle, SelectedSong == null
            ? catalogSnapshot == null ? T("Loading songs…", "Cargando canciones…") : T("Choose a song", "Elige una canción")
            : DisplayTitle(SelectedSong));
        SetText(songMetadata, SelectedSong == null ? "" : SongMetadata(SelectedSong));
        SetButton(browse, canChange, Raised);

        SetButton(leftHand, game.CanConfigureHands, game.PracticeHand == HandSelection.Left ? Violet : Control);
        SetButton(bothHands, game.CanConfigureHands, game.PracticeHand == HandSelection.Both ? Violet : Control);
        SetButton(rightHand, game.CanConfigureHands, game.PracticeHand == HandSelection.Right ? Violet : Control);
        SetButton(learn, canChange && pianoReady && midiReady, game.LearningMode ? Violet : Control);
        SetButton(play, canChange && PianoSelected, !game.LearningMode ? Violet : Control);
        SetText(speed, Mathf.RoundToInt(game.gameSpeed * 100f) + "%");
        SetButton(slower, canChange && game.gameSpeed > 0.3f, Raised);
        SetButton(faster, canChange && game.gameSpeed < 1.5f, Raised);

        statusDot.color = InstrumentReady && midi?.IsCalibrating != true ? Green : Amber;
        SetText(practiceStatus, PianoSelected
            ? (pianoReady ? T("Piano ready", "Piano listo") : T("Set up piano", "Ajusta el piano")) + " · " +
              (midiReady ? T("MIDI ready", "MIDI listo") : midi?.IsCalibrating == true
                  ? T("Calibrating MIDI", "Calibrando MIDI") : T("MIDI optional", "MIDI opcional"))
            : T("Bongos ready", "Bongós listos"));
        SetButton(setup, canChange, Control);
        SetButton(settings, canChange, Control);
        SetText(guideSummary, PianoSelected ? (game.ReferenceSoundEnabled
            ? T("Guide sound on", "Guía activada") : T("Guide sound off", "Guía desactivada")) : "");
        var blocker = GetStartBlocker();
        SetButton(start, blocker == StartBlocker.None, blocker == StartBlocker.None ? Violet : Control);
        SetText(startHint, blocker == StartBlocker.None && pianoReady && !midiReady
            ? T("Learn mode requires MIDI calibration.", "El modo Aprender requiere calibrar MIDI.")
            : ExplainBlocker(blocker));
    }

    private string ExplainBlocker(StartBlocker blocker)
    {
        switch (blocker)
        {
            case StartBlocker.Input: return T("Waiting for input", "Esperando control");
            case StartBlocker.Setup: return T("Set up your piano to start.", "Ajusta el piano para empezar.");
            case StartBlocker.Catalog: return T("Loading the song library…", "Cargando la biblioteca…");
            case StartBlocker.NoSong: return T("Choose a song to start.", "Elige una canción para empezar.");
            case StartBlocker.Loading: return T("Loading the selected song…", "Cargando la canción elegida…");
            case StartBlocker.LoadFailed: return T("Song unavailable. Try again in Browse songs.", "Canción no disponible. Intenta en Ver canciones.");
            case StartBlocker.Midi: return T("Calibrate MIDI for Learn mode.", "Calibra MIDI para el modo Aprender.");
            case StartBlocker.Range:
                return game.PracticeHand == HandSelection.Both
                    ? T("No notes in the keyboard range.", "No hay notas en el rango del teclado.")
                    : T("No notes for this hand in range.", "No hay notas de esta mano en el rango.");
            default: return "";
        }
    }

    private void RefreshLibrary()
    {
        libraryPage = Mathf.Clamp(libraryPage, 0, PageCount - 1);
        bool loading = catalogSnapshot == null;
        bool hasWarning = !string.IsNullOrEmpty(SongLoader.ImportStatus);
        bool interrupted = loading && hasWarning && !SongLoader.ImportStatus.StartsWith("Loading", StringComparison.Ordinal);
        SetText(libraryCount, loading ? "" : (catalog.Count == 1 ? T("1 song", "1 canción")
            : T($"{catalog.Count} songs", $"{catalog.Count} canciones")));
        SetText(libraryEmpty, loading ? (interrupted
                ? T("The catalog load was interrupted. Restart the app to try again.",
                    "Se interrumpió la carga. Reinicia la app para volver a intentarlo.")
                : T("Loading songs and personal MIDI…", "Cargando canciones y archivos MIDI…"))
            : catalog.Count == 0 ? T("No songs available. Add MIDI files to the Music folder and restart.",
                "No hay canciones. Añade archivos MIDI a la carpeta Music y reinicia.") : "");
        for (int i = 0; i < songRows.Length; i++)
        {
            int index = libraryPage * SongsPerPage + i;
            var row = songRows[i];
            bool exists = index < catalog.Count;
            row.Button.gameObject.SetActive(exists);
            if (!exists) continue;
            var song = catalog[index];
            SetText(row.Title, DisplayTitle(song));
            SetText(row.Metadata, SongMetadata(song) + (song.IsExternal ? T(" · Personal MIDI", " · MIDI personal") : ""));
            SetButton(row.Button, CanNavigate, ReferenceEquals(song, SelectedSong) ? Violet : Control);
        }
        SetText(pageNumber, catalog.Count == 0 ? "" : $"{libraryPage + 1} / {PageCount}");
        SetButton(previousPage, CanNavigate && libraryPage > 0, Control);
        SetButton(nextPage, CanNavigate && libraryPage + 1 < PageCount, Control);
        SetText(libraryStatus, !loading && hasWarning
            ? T("Catalog warnings · See Music/import-report.txt", "Avisos del catálogo · Consulta Music/import-report.txt") : "");
    }

    private void RefreshSetup()
    {
        var definer = menus.PianoDefiner;
        bool picking = setupStep == SetupStep.Choices;
        bool automatic = setupStep == SetupStep.Automatic;
        bool canConfirm = definer?.CanConfirm == true;
        int pointCount = definer?.CapturedPointCount ?? 0;
        setupChoices.gameObject.SetActive(picking);
        setupCapture.gameObject.SetActive(!picking);
        if (picking)
        {
            bool pianoReady = game.IsPiano && menus.PianoReady;
            SetText(setupState, pianoReady ? T("Piano ready", "Piano listo") : T("Keyboard position", "Posición del teclado"));
            SetButton(beginAutomatic, CanNavigate && definer != null && detector != null, Violet);
            SetButton(beginManual, CanNavigate && definer != null, Control);
            SetText(midiSetupStatus, pianoReady ? midi?.StatusMessage ?? T("MIDI unavailable.", "MIDI no disponible.")
                : T("Confirm the keyboard position first. Calibrate MIDI to use Learn mode.",
                    "Primero confirma la posición del teclado. Calibra MIDI para usar el modo Aprender."));
            SetButton(calibrate, CanNavigate && pianoReady && midi?.IsConnected == true && !midi.IsCalibrating, Control);
            SetButton(reverse, CanNavigate && pianoReady && MidiReady, Control);
            return;
        }
        capture.gameObject.SetActive(automatic);
        resetPoints.gameObject.SetActive(!automatic);
        SetText(setupInstruction, automatic
            ? T("Step back until you can see the full keyboard. Center all the keys before capturing.",
                "Aléjate hasta ver todo el teclado. Centra todas las teclas antes de capturar.")
#if UNITY_EDITOR || UNITY_STANDALONE_WIN
            : T("Click the three keyboard corners, or press F2 to use the simulated piano.",
                "Haz clic en tres esquinas del teclado, o pulsa F2 para usar el piano simulado."));
#else
            : T("Mark three corners with the right trigger. You can look at one corner at a time.",
                "Marca tres esquinas con el gatillo derecho. Puedes mirar una esquina a la vez."));
#endif
        SetText(setupStatus, automatic ? detector?.StatusMessage ?? T("Camera unavailable.", "Cámara no disponible.")
            : ManualInstruction(pointCount, canConfirm));
        SetButton(capture, CanNavigate && automatic && detector != null, Control);
        SetButton(resetPoints, CanNavigate && !automatic && pointCount > 0, Control);
        SetButton(confirm, CanNavigate && canConfirm, Violet);
        SetButton(cancelStep, CanNavigate, Control);
    }

    private static string ManualInstruction(int points, bool canConfirm)
    {
        if (canConfirm) return T("Check the outline against your keys. Adjust the corners if needed, then confirm.",
            "Revisa que el contorno coincida con tus teclas. Ajusta las esquinas si hace falta y confirma.");
        switch (points)
        {
            case 0: return T("1 of 3 · Back-left corner", "1 de 3 · Esquina posterior izquierda");
            case 1: return T("2 of 3 · Back-right corner", "2 de 3 · Esquina posterior derecha");
            default: return T("3 of 3 · Front-right corner", "3 de 3 · Esquina delantera derecha");
        }
    }

    private void RefreshSettings()
    {
        SetButton(guideSound, CanNavigate && PianoSelected, Control);
        SetText(guideState, game.ReferenceSoundEnabled ? T("On", "Activo") : T("Off", "Inactivo"));
        SetButton(keyFeedback, CanNavigate && game.IsPiano && menus.PianoReady && midi?.IsInitialized == true, Control);
        SetText(feedbackState, midi?.FeedbackEnabled == true ? T("On", "Activos") : T("Off", "Inactivos"));
        SetButton(splitDown, game.CanConfigureHands && game.SplitPitch > 0, Raised);
        SetButton(splitUp, game.CanConfigureHands && game.SplitPitch < 127, Raised);
        SetText(splitValue, MidiNotes.Format(game.SplitPitch) + (game.SplitPitch == PianoHandPartition.DefaultSplitPitch
            ? T(" · Default", " · Por defecto") : ""));
    }

    private void RefreshPause()
    {
        SetText(pauseSong, game.LoadedSong == null ? "" : DisplayTitle(game.LoadedSong));
        SetButton(resume, CanContinuePaused, Violet);
        SetButton(restart, CanContinuePaused && game.HasLoadedSong, Control);
        SetButton(chooseSong, Paused && game.InputAllowed, Control);
        SetText(pauseStatus, game.LearningMode && !MidiReady ? midi?.StatusMessage : "");
    }

    private void RefreshAdvanced()
    {
        bool enabled = ProjectConfig.Settings.enableMultiplayer;
        SetButton(usePiano, CanNavigate && menus.PianoReady, Control);
        SetButton(useBongos, CanNavigate && menus.BongosReady, Control);
        SetButton(startBongoSetup, CanNavigate, Control);
        SetButton(multiplayer, CanNavigate && InstrumentReady && enabled, Control);
        multiplayer.gameObject.SetActive(enabled);
        SetText(multiplayerHint, enabled && !InstrumentReady
            ? T("Set up an instrument to enter multiplayer.", "Ajusta un instrumento para entrar al modo multijugador.") : "");
    }

    private static string DisplayTitle(SongData song) => song.title?.Replace('\r', ' ').Replace('\n', ' ') ?? "";

    private static string SongMetadata(SongData song)
    {
        string time = song.duration == 0 ? T("Time unknown", "Duración desconocida")
            : (song.duration >= 3600 ? $"{song.duration / 3600}:{song.duration / 60 % 60:00}" : $"{song.duration / 60}") +
              $":{song.duration % 60:00}";
        return Difficulty(song.difficulty) + " · " + time;
    }

    private static string Difficulty(SongDifficulty difficulty)
    {
        switch (difficulty)
        {
            case SongDifficulty.Beginner: return T("Beginner", "Principiante");
            case SongDifficulty.Easy: return T("Easy", "Fácil");
            case SongDifficulty.Medium: return T("Medium", "Intermedia");
            case SongDifficulty.Hard: return T("Hard", "Difícil");
            case SongDifficulty.VeryHard: return T("Very hard", "Muy difícil");
            case SongDifficulty.Professional: return T("Professional", "Profesional");
            default: return T("Unrated", "Sin nivel");
        }
    }

    private void Header(RectTransform panel, string backName, string title, string translated, UnityAction onBack)
    {
        IconButton(panel, backName, "back", "‹", 26, 18, 48, 43, onBack);
        CopyLabel(panel, "ViewTitle", title, translated, 88, 14, 305, 50, 31, bold: true);
        Line(panel, 29, 73, 363, 1);
    }

    private Button NavigationButton(RectTransform panel, string name, string label, string translated,
        string icon, string fallback, int y, UnityAction action, float labelWidth = 255, bool chevron = true)
    {
        var button = MakeButton(panel, name, 26, y, 373, 48, action, Control, 14);
        float x = 16;
        if (icon != null)
        {
            AddIcon(button.transform, icon, fallback, 23, 9, 29);
            x = 77;
        }
        CopyLabel(button.transform, "Label", label, translated, x, 0, labelWidth, 48, 22, bold: true);
        if (chevron) AddIcon(button.transform, "chevron", "›", 337, 11, 26);
        return button;
    }

    private Button IconButton(Transform parent, string name, string icon, string fallback,
        float x, float y, float w, float h, UnityAction action)
    {
        var button = MakeButton(parent, name, x, y, w, h, action, Raised);
        const int size = 24;
        AddIcon(button.transform, icon, fallback, (w - size) / 2, (h - size) / 2, size);
        return button;
    }

    private Button LocalButton(Transform parent, string name, string text, string translated,
        float x, float y, float w, float h, int size, UnityAction action, Color? tone = null, int radius = 12)
    {
        var button = MakeButton(parent, name, x, y, w, h, action, tone ?? Control, radius);
        var label = Label(button.transform, "Label", "", 5, 0, w - 10, h, size, bold: true,
            alignment: TextAlignmentOptions.Center, minimum: size * 0.80f);
        AddCopy(label, text, translated);
        return button;
    }

    private Button ButtonWithLabel(Transform parent, string name, string text,
        float x, float y, float w, float h, int size, UnityAction action)
    {
        var button = MakeButton(parent, name, x, y, w, h, action, Control);
        Label(button.transform, "Label", text, 5, 0, w - 10, h, size, bold: true,
            alignment: TextAlignmentOptions.Center);
        return button;
    }

    private Button MakeButton(Transform parent, string name, float x, float y, float w, float h,
        UnityAction action, Color tone, int radius = 12)
    {
        var rect = Child(parent, name, x, y, w, h);
        var image = Rounded(rect, tone, radius);
        image.raycastTarget = true;
        var button = rect.gameObject.AddComponent<Button>();
        button.targetGraphic = image;
        button.transition = Selectable.Transition.ColorTint;
        button.navigation = new Navigation { mode = Navigation.Mode.None };
        button.colors = new ColorBlock
        {
            normalColor = Color.white,
            highlightedColor = new Color(0.92f, 0.92f, 1f, 1f),
            selectedColor = new Color(0.92f, 0.92f, 1f, 1f),
            pressedColor = new Color(0.74f, 0.74f, 0.88f, 1f),
            disabledColor = new Color(0.72f, 0.72f, 0.75f, 0.7f),
            colorMultiplier = 1f,
            fadeDuration = 0.08f
        };
        button.onClick.AddListener(action);
        return button;
    }

    private static void SetButton(Button button, bool enabled, Color tone)
    {
        if (button.interactable != enabled) button.interactable = enabled;
        if (button.targetGraphic.color != tone) button.targetGraphic.color = tone;
    }

    private TMP_Text CopyLabel(Transform parent, string name, string english, string spanish,
        float x, float y, float w, float h, float size, TextAlignmentOptions alignment = TextAlignmentOptions.Left,
        bool bold = false, Color? tint = null, bool wrap = false)
    {
        var label = Label(parent, name, "", x, y, w, h, size, tint, bold, alignment, wrap,
            size >= 30 ? size * 0.76f : 0);
        AddCopy(label, english, spanish);
        return label;
    }

    private void AddCopy(TMP_Text label, string english, string spanish)
    {
        var copy = new Copy(label, english, spanish);
        localizedLabels.Add(copy);
        copy.Refresh();
    }

    private TMP_Text Label(Transform parent, string name, string text,
        float x, float y, float w, float h, float size, Color? tint = null,
        bool bold = false, TextAlignmentOptions alignment = TextAlignmentOptions.Left, bool wrap = false, float minimum = 0)
    {
        var rect = Child(parent, name, x, y, w, h);
        var label = rect.gameObject.AddComponent<TextMeshProUGUI>();
        if (font != null) label.font = font;
        label.fontSize = size;
        label.fontStyle = bold ? FontStyles.Bold : FontStyles.Normal;
        label.color = tint ?? TextColor;
        label.alignment = alignment;
        label.textWrappingMode = wrap ? TextWrappingModes.Normal : TextWrappingModes.NoWrap;
        label.overflowMode = TextOverflowModes.Ellipsis;
        label.richText = false;
        label.parseCtrlCharacters = false;
        label.raycastTarget = false;
        if (minimum > 0)
        {
            label.enableAutoSizing = true;
            label.fontSizeMin = minimum;
            label.fontSizeMax = size;
        }
        label.text = text;
        return label;
    }

    private static void SetText(TMP_Text label, string text)
    {
        if (label != null && label.text != text) label.text = text ?? "";
    }

    private void AddIcon(Transform parent, string name, string fallback, float x, float y, float size)
    {
        if (!icons.TryGetValue(name, out var sprite))
            icons.Add(name, sprite = Resources.Load<Sprite>("PracticeIcons/" + name));
        if (sprite == null)
        {
            Label(parent, name + "Icon", fallback, x, y, size, size, size, bold: true, alignment: TextAlignmentOptions.Center);
            return;
        }
        var image = Child(parent, name + "Icon", x, y, size, size).gameObject.AddComponent<Image>();
        image.sprite = sprite;
        image.color = TextColor;
        image.raycastTarget = false;
    }

    private static void Line(Transform parent, float x, float y, float w, float h)
    {
        var image = Child(parent, "Divider", x, y, w, h).gameObject.AddComponent<Image>();
        image.color = Divider;
        image.raycastTarget = false;
    }

    private Image Rounded(RectTransform rect, Color color, int radius)
    {
        var image = rect.gameObject.AddComponent<Image>();
        image.sprite = RoundedSprite(radius);
        image.type = Image.Type.Sliced;
        image.color = color;
        image.raycastTarget = false;
        return image;
    }

    private Sprite RoundedSprite(int radius)
    {
        if (roundedSprites.TryGetValue(radius, out var sprite)) return sprite;
        int size = radius * 2 + 4;
        var pixels = new Color32[size * size];
        for (int y = 0; y < size; y++)
        {
            float dy = Mathf.Max(Mathf.Max(radius - y - 0.5f, y + 0.5f - (size - radius)), 0);
            for (int x = 0; x < size; x++)
            {
                float dx = Mathf.Max(Mathf.Max(radius - x - 0.5f, x + 0.5f - (size - radius)), 0);
                float alpha = Mathf.Clamp01(radius + 0.5f - Mathf.Sqrt(dx * dx + dy * dy));
                pixels[y * size + x] = new Color32(255, 255, 255, (byte)Mathf.RoundToInt(alpha * 255));
            }
        }
        var texture = new Texture2D(size, size, TextureFormat.RGBA32, false) { wrapMode = TextureWrapMode.Clamp };
        texture.SetPixels32(pixels);
        texture.Apply(false, true);
        sprite = Sprite.Create(texture, new Rect(0, 0, size, size), new Vector2(0.5f, 0.5f),
            100, 0, SpriteMeshType.FullRect, Vector4.one * (radius + 1));
        roundedSprites.Add(radius, sprite);
        return sprite;
    }

    private void DestroyLayoutAssets()
    {
        foreach (var sprite in roundedSprites.Values)
        {
            if (sprite == null) continue;
            ReleaseLayoutObject(sprite.texture);
            ReleaseLayoutObject(sprite);
        }
        foreach (var panel in panels.Values)
            if (panel.Rect != null) ReleaseLayoutObject(panel.Rect.gameObject);
    }

    private static void ReleaseLayoutObject(UnityEngine.Object value)
    {
        if (value == null) return;
        if (Application.isPlaying) Destroy(value);
        else DestroyImmediate(value); // The APK preflight instantiates the shell in Edit Mode.
    }

    private static RectTransform Child(Transform parent, string name, float x, float y, float w, float h)
    {
        var rect = (RectTransform)new GameObject(name, typeof(RectTransform)).transform;
        rect.gameObject.layer = parent.gameObject.layer;
        rect.SetParent(parent, false);
        rect.anchorMin = rect.anchorMax = rect.pivot = new Vector2(0, 1);
        rect.sizeDelta = new Vector2(w, h);
        rect.anchoredPosition = new Vector2(x, -y);
        return rect;
    }
}
