using System;
using System.Linq;
using TMPro;
using UnityEngine;
using UnityEngine.UI;

/// <summary>Practice options use the existing lobby canvas and its Quest interaction handlers.</summary>
public sealed class PracticeOptionsUI : MonoBehaviour
{
    private RhythmGameManager game;
    private PianoMidiFeedback midi;
    private GameObject lobby;
    private Button learningButton, soundButton;
    private TMP_Text learningText, soundText, hintText;
    private bool initialized;

    public void Initialize(RhythmGameManager manager, PianoMidiFeedback feedback)
    {
        if (initialized || manager == null || manager.menucontroller == null) return;
        game = manager;
        midi = feedback;
        lobby = game.menucontroller.singlePlayerLobbyMenu;
        if (lobby == null) return;
        var start = lobby.GetComponentsInChildren<Button>(true).FirstOrDefault(button => button.name == "StartButton");
        if (start == null || start.GetComponentInChildren<TMP_Text>(true) == null)
        {
            Debug.LogWarning("Practice options unavailable: lobby Start button template is missing.");
            return;
        }
        var panel = start.transform.parent as RectTransform;
        if (panel == null) return;
        var layout = panel.GetComponent<LayoutGroup>();
        if (layout != null) layout.enabled = false;
        panel.sizeDelta = new Vector2(panel.sizeDelta.x, 430);
        Position(start.GetComponent<RectTransform>(), new Vector2(0, -110), new Vector2(310, 62));
        learningButton = CreateButton(start, panel, "LearningModeButton", 80, ToggleLearning, out learningText);
        soundButton = CreateButton(start, panel, "ReferenceSoundButton", 0, ToggleSound, out soundText);
        hintText = CreateLabel(start, panel, "PracticeHint", 155, 24);
        var controls = CreateLabel(start, panel, "PracticeControls", -175, 19);
        controls.text = "B: MIDI calibration   X: reverse keys   Y: key feedback";
        initialized = true;
        Refresh();
    }

    private static Button CreateButton(Button template, RectTransform parent, string name,
        float y, UnityEngine.Events.UnityAction action, out TMP_Text label)
    {
        var button = Instantiate(template, parent, false);
        button.name = name;
        // RemoveAllListeners leaves serialized Start/countdown callbacks intact.
        button.onClick = new Button.ButtonClickedEvent();
        button.onClick.AddListener(action);
        Position(button.GetComponent<RectTransform>(), new Vector2(0, y), new Vector2(480, 62));
        label = button.GetComponentInChildren<TMP_Text>(true);
        label.fontSize = 28;
        label.enableAutoSizing = false;
        button.gameObject.SetActive(true);
        return button;
    }

    private static TMP_Text CreateLabel(Button template, RectTransform parent, string name, float y, float fontSize)
    {
        var label = Instantiate(template.GetComponentInChildren<TMP_Text>(true), parent, false);
        label.name = name;
        label.raycastTarget = false;
        label.fontSize = fontSize;
        label.enableAutoSizing = false;
        label.alignment = TextAlignmentOptions.Center;
        Position(label.rectTransform, new Vector2(0, y), new Vector2(560, 52));
        label.gameObject.SetActive(true);
        return label;
    }

    private static void Position(RectTransform rect, Vector2 position, Vector2 size)
    {
        rect.anchorMin = rect.anchorMax = new Vector2(0.5f, 0.5f);
        rect.pivot = new Vector2(0.5f, 0.5f);
        rect.anchoredPosition = position;
        rect.sizeDelta = size;
    }

    private void ToggleLearning()
    {
        game.SetLearningMode(!game.LearningMode);
        Refresh();
    }

    private void ToggleSound()
    {
        game.SetReferenceSoundEnabled(!game.ReferenceSoundEnabled);
        Refresh();
    }

    private void Update()
    {
        if (initialized && lobby.activeInHierarchy) Refresh();
    }

    private void Refresh()
    {
        if (!initialized) return;
        bool piano = game.IsPiano;
        bool canChange = game.CanSelectInstrument;
        bool calibrated = midi != null && midi.IsCalibrated;
        learningButton.interactable = canChange && piano && (calibrated || game.LearningMode);
        soundButton.interactable = canChange && piano;
        learningText.text = game.LearningMode ? "Learning: ON" : "Learning: OFF";
        soundText.text = game.ReferenceSoundEnabled ? "Piano guide sound: ON" : "Piano guide sound: OFF";
        hintText.text = !piano ? "Piano practice options"
            : !calibrated ? "Press B and play the two end keys\nto enable Learning mode."
            : game.LearningMode ? "Play each note or chord to continue."
            : "Choose a practice mode, then Start.";
    }
}
