using System;
using System.Collections.Generic;
using System.Linq;
using ARMusician.Midi;
using Oculus.Interaction.Surfaces;
using UnityEngine;

public enum PracticeView { Hidden, Prepare, Library, PianoSetup, Settings, Pause, Advanced }

/// <summary>Single-player practice navigation on the scene's existing world-space canvases.</summary>
public sealed partial class PracticeOptionsUI : MonoBehaviour
{
    private const string LanguagePreference = "ARMusician.AppLanguage";
    private const int SongsPerPage = 6;

    private enum SetupStep { Choices, Automatic, Manual }
    private enum StartBlocker { None, Input, Setup, Catalog, NoSong, Loading, LoadFailed, Midi, Range }

    private RhythmGameManager game;
    private MenuControllerGeneric menus;
    private PianoMidiFeedback midi;
    private CVPlaneFinderGeneric detector;
    private CountdownUIGeneric countdown;
    private SongsUIHandlerGeneric legacySongs;
    private MultiplayerControllerGeneric multiplayerController;

    private readonly Dictionary<PracticeView, Panel> panels = new Dictionary<PracticeView, Panel>();
    private readonly Dictionary<GameObject, CanvasHost> hosts = new Dictionary<GameObject, CanvasHost>();
    private List<SongData> catalog = new List<SongData>();
    private LinkedList<SongData> catalogSnapshot;
    private Panel visiblePanel;
    private SetupStep setupStep;
    private int libraryPage;
    private int previousPointCount;
    private bool previousCanConfirm;
    private float refreshAfter;
    private SongData requestedSong;
    private SongData previouslyLoadedSong;
    private bool playableNotes;
    private (int Song, HandSelection Hand, int Split, bool Piano, bool Ready, bool MidiReady, int First, int Last)? rangeKey;

    public PracticeView CurrentView { get; private set; }
    public bool IsVisible => visiblePanel != null && ViewAllowed && visiblePanel.Rect.gameObject.activeInHierarchy;
    public SongData SelectedSong { get; private set; }
    public bool IsSongLoading => game != null && game.IsSongLoading;
    public bool CanStart => CurrentView == PracticeView.Prepare && GetStartBlocker() == StartBlocker.None;

    private bool IsMultiplayer => ProjectConfig.Settings.enableMultiplayer && ProjectConfig.Settings.useMultiplayer;
    private bool Idle => game != null && game.State == RhythmGameManager.PlaybackState.Idle;
    private bool Paused => game != null && game.State == RhythmGameManager.PlaybackState.Paused;
    private bool ViewAllowed => !IsMultiplayer && (CurrentView == PracticeView.Pause ? Paused : Idle);
    private bool CanNavigate => Idle && game.InputAllowed && !IsMultiplayer;
    private bool PianoSelected => game.IsPiano || !menus.instrumentDefined;
    private bool InstrumentReady => game.IsPiano ? menus.PianoReady : menus.BongosReady && menus.instrumentDefined;
    private bool MidiReady => midi != null && midi.IsCalibrated;
    private bool CanContinuePaused => Paused && game.InputAllowed && countdown != null && (!game.LearningMode || MidiReady);

    public void Initialize(RhythmGameManager manager, PianoMidiFeedback feedback)
    {
        if (game != null || manager == null || manager.menucontroller == null) return;
        game = manager;
        menus = manager.menucontroller;
        midi = feedback;
        detector = FindFirstObjectByType<CVPlaneFinderGeneric>(FindObjectsInactive.Include);
        countdown = FindObjectsByType<CountdownUIGeneric>(FindObjectsInactive.Include, FindObjectsSortMode.None)
            .FirstOrDefault(candidate => candidate.rhythmGameManager == game);
        legacySongs = menus.songMenu.GetComponentInChildren<SongsUIHandlerGeneric>(true);
        multiplayerController = FindFirstObjectByType<MultiplayerControllerGeneric>(FindObjectsInactive.Include);
        AppText.SetLanguage(PlayerPrefs.GetInt(LanguagePreference) == (int)AppLanguage.Spanish
            ? AppLanguage.Spanish : AppLanguage.English);
        BuildPanels();
        menus.RegisterPracticeShell(this);
        AppText.Changed += OnLanguageChanged;
        SongLoader.OnSongsLoaded += OnSongsLoaded;
        PianoManagerInstrumentDefiner.OnPlaneDefined += OnPianoDefined;

        // The practice shell is the single-player entry point. Multiplayer remains in Settings.
        SetSinglePlayer();
        OnSongsLoaded(SongLoader.CurrentSongs);
        ShowPrepare();
    }

    private void OnDestroy()
    {
        AppText.Changed -= OnLanguageChanged;
        SongLoader.OnSongsLoaded -= OnSongsLoaded;
        PianoManagerInstrumentDefiner.OnPlaneDefined -= OnPianoDefined;
        if (midi != null) midi.SuppressStatusLabel = false;
        foreach (var host in hosts.Values) host.RestoreLegacy();
        DestroyLayoutAssets();
    }

    private void OnPianoDefined(DefinedPlane plane) => rangeKey = null;

    private void Update()
    {
        if (visiblePanel == null) return;
        if (!ViewAllowed)
        {
            menus.HideAllMenus();
            return;
        }
        if (CurrentView == PracticeView.PianoSetup)
        {
            int points = menus.PianoDefiner?.CapturedPointCount ?? 0;
            bool canConfirm = menus.PianoDefiner?.CanConfirm == true;
            if (previousPointCount != points || previousCanConfirm != canConfirm)
            {
                previousPointCount = points;
                previousCanConfirm = canConfirm;
                RefreshSetup();
            }
        }
        if (Time.unscaledTime < refreshAfter) return;
        refreshAfter = Time.unscaledTime + 0.1f;
        SynchronizeSong();
        if (CurrentView == PracticeView.Prepare && CanNavigate && SelectedSong != null &&
            game.SongLoadVersion == 0)
            RequestSong(SelectedSong);
        RefreshView();
    }

    private void LateUpdate()
    {
        if (visiblePanel == null) return;
        if (!ViewAllowed)
        {
            menus.HideAllMenus();
            return;
        }
#if UNITY_EDITOR || UNITY_STANDALONE_WIN
        PositionPanel(visiblePanel);
#else
        // Meta's canvas driver can resize its hit surface when layout changes.
        // Keep the interaction bounds scoped to the visible compact panel.
        visiblePanel.Host.SetBounds(visiblePanel.Rect.localPosition,
            visiblePanel.Rect.rect.size * visiblePanel.Rect.localScale.x);
#endif
    }

    internal void Hide()
    {
        if (visiblePanel != null)
        {
            visiblePanel.Rect.gameObject.SetActive(false);
            visiblePanel.Host.RestoreBounds();
        }
        visiblePanel = null;
        CurrentView = PracticeView.Hidden;
        if (midi != null) midi.SuppressStatusLabel = false;
    }

    internal void RestoreLegacy(GameObject host)
    {
        if (host != null && hosts.TryGetValue(host, out var canvas)) canvas.RestoreLegacy();
    }

    private void Show(PracticeView view)
    {
        menus.HideAllMenus();
        visiblePanel = panels[view];
        CurrentView = view;
        visiblePanel.Host.SuppressLegacy();
        visiblePanel.Rect.gameObject.SetActive(true);
        visiblePanel.Host.Root.SetActive(true);
        // Activating a previously inactive canvas runs the Meta clipper driver's
        // Awake, which restores its legacy bounds. Apply our bounds afterwards.
        PositionPanel(visiblePanel);
        if (midi != null) midi.SuppressStatusLabel = true;
        SynchronizeSong();
        RefreshView();
    }

    // The manager can restore preparation or pause while focus is unavailable.
    // The corresponding actions remain guarded until input is active again.
    public void ShowPrepare()
    {
        if (!Idle || IsMultiplayer) return;
        Show(PracticeView.Prepare);
    }

    internal void RestorePreparation()
    {
        if (!IsVisible) ShowPrepare();
    }

    public void ShowLibrary()
    {
        if (!Idle || IsMultiplayer) return;
        if (CurrentView != PracticeView.Library)
        {
            SynchronizeSong();
            libraryPage = Mathf.Max(0, catalog.IndexOf(SelectedSong)) / SongsPerPage;
        }
        Show(PracticeView.Library);
    }

    public void ShowSettings()
    {
        if (CanNavigate) Show(PracticeView.Settings);
    }

    public void ShowPianoSetup()
    {
        if (!CanNavigate) return;
        setupStep = SetupStep.Choices;
        Show(PracticeView.PianoSetup);
    }

    internal void ShowPause()
    {
        if (Paused && !IsMultiplayer) Show(PracticeView.Pause);
    }

    public void BeginManualSetup()
    {
        if (CanNavigate) menus.StartPianoSetup();
    }

    public void BeginAutomaticSetup()
    {
        if (CanNavigate) menus.ShowCVPlaneDefinitionMenu();
    }

    internal void ShowSetupCapture(bool automatic)
    {
        if (!CanNavigate) return;
        setupStep = automatic ? SetupStep.Automatic : SetupStep.Manual;
        Show(PracticeView.PianoSetup);
        menus.ActivatePianoDefinition(automatic);
        RefreshView();
    }

    public void ConfirmPianoPose()
    {
        if (CanNavigate && CurrentView == PracticeView.PianoSetup)
            menus.PianoDefiner?.ConfirmFinalPlane();
    }

    private void CaptureKeyboard()
    {
        if (CanNavigate && CurrentView == PracticeView.PianoSetup && setupStep == SetupStep.Automatic)
            detector?.CaptureKeyboard();
    }

    private void ResetManualPoints()
    {
        if (CanNavigate && CurrentView == PracticeView.PianoSetup && setupStep == SetupStep.Manual)
            menus.ActivatePianoDefinition(false);
        RefreshView();
    }

    private void RequestMidiCalibration()
    {
        if (CanNavigate && CurrentView == PracticeView.PianoSetup) midi?.RequestCalibration();
        RefreshView();
    }

    private void ReverseKeyboard()
    {
        if (CanNavigate && CurrentView == PracticeView.PianoSetup) midi?.ReverseKeyboardAlignment();
        RefreshView();
    }

    public void SelectSong(SongData song)
    {
        if (!CanNavigate || song == null || !catalog.Contains(song)) return;
        RequestSong(song);
        ShowPrepare();
    }

    private void RequestSong(SongData song)
    {
        SelectedSong = requestedSong = song;
        game.updateSong(song);
    }

    private void OnSongsLoaded(LinkedList<SongData> songs)
    {
        catalogSnapshot = SongLoader.CurrentSongs;
        catalog = songs == null ? new List<SongData>() : songs.ToList();
        if (!catalog.Contains(SelectedSong))
            SelectedSong = catalog.Contains(legacySongs?.SelectedSong) ? legacySongs.SelectedSong : catalog.FirstOrDefault();
        if (visiblePanel != null) RefreshView();
    }

    private void SynchronizeSong()
    {
        if (!ReferenceEquals(catalogSnapshot, SongLoader.CurrentSongs)) OnSongsLoaded(SongLoader.CurrentSongs);
        var loaded = game.LoadedSong;
        if (ReferenceEquals(previouslyLoadedSong, loaded)) return;
        previouslyLoadedSong = loaded;
        if (loaded != null && catalog.Contains(loaded))
        {
            SelectedSong = loaded;
            requestedSong = null;
        }
    }

    public void StartPractice()
    {
        if (!CanStart || countdown == null) return;
        menus.HideAllMenus();
        countdown.StartCountdown();
    }

    public void ResumePractice()
    {
        if (CurrentView != PracticeView.Pause || !CanContinuePaused) return;
        menus.HideAllMenus();
        countdown.ResumeCountdown();
    }

    public void RestartPractice()
    {
        if (CurrentView != PracticeView.Pause || !CanContinuePaused || !game.HasLoadedSong) return;
        game.Quit();
        menus.HideAllMenus();
        countdown.StartCountdown();
    }

    public void ChooseSong()
    {
        if (CurrentView != PracticeView.Pause || !Paused || !game.InputAllowed) return;
        game.Quit();
        if (CurrentView != PracticeView.Library) ShowLibrary();
    }

    public void SetLanguage(AppLanguage language)
    {
        if (language != AppLanguage.English && language != AppLanguage.Spanish) return;
        if (AppText.Language == language) return;
        PlayerPrefs.SetInt(LanguagePreference, (int)language);
        PlayerPrefs.Save();
        AppText.SetLanguage(language);
    }

    private void OnLanguageChanged()
    {
        foreach (var label in localizedLabels) label.Refresh();
        RefreshView();
    }

    private void SelectHand(HandSelection selection)
    {
        game.SetHandSelection(selection);
        RefreshView();
    }

    private void SelectLearning(bool enabled)
    {
        game.SetLearningMode(enabled);
        RefreshView();
    }

    private void AdjustSpeed(int step)
    {
        if (CanNavigate)
        {
            int percent = Mathf.RoundToInt(game.gameSpeed * 100f);
            game.gameSpeed = Mathf.Clamp(percent + step, 30, 150) / 100f;
        }
        RefreshView();
    }

    private void AdjustSplit(int step)
    {
        game.SetSplitPitch(game.SplitPitch + step);
        RefreshView();
    }

    private void ToggleGuide()
    {
        if (CanNavigate && PianoSelected) game.SetReferenceSoundEnabled(!game.ReferenceSoundEnabled);
        RefreshView();
    }

    private void ToggleKeyFeedback()
    {
        if (CanNavigate && midi != null) midi.SetFeedbackEnabled(!midi.FeedbackEnabled);
        RefreshView();
    }

    private void ChangeLibraryPage(int direction)
    {
        if (!CanNavigate) return;
        libraryPage = Mathf.Clamp(libraryPage + direction, 0, PageCount - 1);
        RefreshView();
    }

    private int PageCount => Mathf.Max(1, (catalog.Count + SongsPerPage - 1) / SongsPerPage);

    private void SelectLibraryRow(int row)
    {
        int index = libraryPage * SongsPerPage + row;
        if (index < catalog.Count) SelectSong(catalog[index]);
    }

    private void ShowAdvanced()
    {
        if (CanNavigate) Show(PracticeView.Advanced);
    }

    private void EnterMultiplayer()
    {
        if (!CanNavigate || !ProjectConfig.Settings.enableMultiplayer || !InstrumentReady) return;
        game.SetLearningMode(false);
        if (multiplayerController != null) multiplayerController.OnMultiplayer();
        else ProjectConfig.Settings.useMultiplayer = true;
        menus.ShowInstrumentSelectorMenu();
    }

    internal void SetSinglePlayer()
    {
        if (multiplayerController != null) multiplayerController.OnSingleplayer();
        else
        {
            ProjectConfig.Settings.useMultiplayer = false;
            ProjectConfig.Settings.master = true;
        }
    }

    private void UsePiano()
    {
        if (!CanNavigate || !menus.PianoReady) return;
        menus.SelectPianoForPractice();
        ShowPrepare();
    }

    private void UseBongos()
    {
        if (!CanNavigate || !menus.BongosReady) return;
        menus.SelectBongosForPractice();
        ShowPrepare();
    }

    private void StartBongoSetup()
    {
        if (CanNavigate) menus.StartBongoSetup();
    }

    private StartBlocker GetStartBlocker()
    {
        if (!CanNavigate || countdown == null) return StartBlocker.Input;
        if (!InstrumentReady || menus.PianoDefiner?.IsDefining == true) return StartBlocker.Setup;
        if (IsSongLoading) return StartBlocker.Loading;
        if (catalogSnapshot == null) return StartBlocker.Catalog;
        if (SelectedSong == null) return StartBlocker.NoSong;
        if (!game.HasLoadedSong || !ReferenceEquals(game.LoadedSong, SelectedSong))
            return ReferenceEquals(requestedSong, SelectedSong) ? StartBlocker.LoadFailed : StartBlocker.Loading;
        if (game.LearningMode && !MidiReady) return StartBlocker.Midi;
        var currentRange = (game.SongLoadVersion, game.PracticeHand, game.SplitPitch, game.IsPiano,
            InstrumentReady, MidiReady, midi?.FirstNote ?? -1, midi?.LastNote ?? -1);
        if (rangeKey != currentRange)
        {
            rangeKey = currentRange;
            playableNotes = game.HasPlayableHandNotes();
        }
        return playableNotes ? StartBlocker.None : StartBlocker.Range;
    }

    // Leave the Meta RayInteractable, PokeInteractable and PointableCanvas attached to
    // their original canvases. Only replace visual children and scope the two hit surfaces.
    private sealed class CanvasHost
    {
        public readonly GameObject Root;
        private readonly (GameObject Child, bool Active)[] legacy;
        private readonly (BoundsClipper Bounds, Vector3 Position, Vector3 Size)[] clippers;

        public CanvasHost(GameObject root)
        {
            Root = root;
            legacy = root.transform.Cast<Transform>()
                .Where(child => !child.name.StartsWith("ISDK_", StringComparison.Ordinal))
                .Select(child => (child.gameObject, child.gameObject.activeSelf)).ToArray();
            clippers = root.GetComponentsInChildren<BoundsClipper>(true)
                .Select(bounds => (bounds, bounds.Position, bounds.Size)).ToArray();
        }

        public void SuppressLegacy()
        {
            foreach (var entry in legacy) entry.Child.SetActive(false);
        }

        public void RestoreLegacy()
        {
            foreach (var entry in legacy)
                if (entry.Child != null) entry.Child.SetActive(entry.Active);
            RestoreBounds();
        }

        public void SetBounds(Vector3 position, Vector2 size)
        {
            foreach (var entry in clippers)
            {
                if (entry.Bounds == null) continue;
                entry.Bounds.Position = entry.Bounds.transform.InverseTransformPoint(Root.transform.TransformPoint(position));
                entry.Bounds.Size = new Vector3(size.x, size.y, entry.Size.z);
            }
        }

        public void RestoreBounds()
        {
            foreach (var entry in clippers)
            {
                if (entry.Bounds == null) continue;
                entry.Bounds.Position = entry.Position;
                entry.Bounds.Size = entry.Size;
            }
        }
    }
}
