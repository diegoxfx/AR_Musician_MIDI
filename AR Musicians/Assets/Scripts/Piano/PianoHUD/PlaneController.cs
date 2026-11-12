using UnityEngine;
using System.Collections.Generic;
using System;

[ExecuteAlways]
public class PlaneController : MonoBehaviour
{
    [Header("Plane Dimensions")]
    public float width = 10f;
    public float height = 1f;

    [Header("Piano Setup")]
    public int totalKeys = 61;       // CT-S200; MIDI calibration verifies the physical endpoints.
    public string leftmostKey = "C2";

    // Internal variable to store the parsed integer of the leftmost key
    private int leftmostKeyIndex = -1;

    public float whiteToBlackRatio = 1.66f;
    public bool reverseHorizontal;

    private Dictionary<int, Vector3> localKeyCenters = new Dictionary<int, Vector3>();
    private Dictionary<int, float> keyWidths = new Dictionary<int, float>();

    // FIXED PATTERN: Starts at C, goes to B. (True = White, False = Black)
    // C, C#, D, D#, E, F, F#, G, G#, A, A#, B
    private static bool[] chromaticPattern =
            { true, false, true, false, true, true, false, true, false, true, false, true };

    void Start()
    {
        reInit();
    }

    public void reInit()
    {
        // 1. Calculate the MIDI number for the start key (e.g., F3 -> 53)
        leftmostKeyIndex = NoteNameToKeyIndex(leftmostKey);
        BuildKeyLayout();
    }

    // Added OnValidate so you see changes in Editor without hitting Play
    void OnValidate()
    {
        reInit();
    }

    void Update()
    {
        if (transform.localScale.x != width || transform.localScale.y != height)
            transform.localScale = new Vector3(width, height, 1f);
    }

    void BuildKeyLayout()
    {
        localKeyCenters.Clear();
        keyWidths.Clear();

        try
        {
            foreach (var key in ARMusician.Midi.KeyboardLayout.Build(leftmostKeyIndex, totalKeys, whiteToBlackRatio))
            {
                localKeyCenters[key.Note] = new Vector3(key.Center, 0.5f, 0f);
                keyWidths[key.Note] = key.Width;
            }
        }
        catch (ArgumentException e) { Debug.LogWarning("Invalid piano layout: " + e.Message); }
    }
    // --- Public API (Signatures Unchanged) ---

    public float GetLocalKeyWidth(int keyIndex)
    {
        // Check dictionary first to prevent index out of bounds errors
        if (keyWidths.ContainsKey(keyIndex))
            return keyWidths[keyIndex];

        return 0f;
    }

    public Vector3 GetLocalKeyPosition(int keyIndex)
    {
        // If the dictionary is empty (e.g., script just loaded), rebuild it
        if (localKeyCenters.Count == 0) reInit();

        // Check dictionary for the exact MIDI note (e.g., 53)
        if (localKeyCenters.ContainsKey(keyIndex))
        {
            var position = localKeyCenters[keyIndex];
            if (reverseHorizontal) position.x = -position.x;
            return position;
        }

        // Return 0 if the note is not part of the keyboard (e.g. note 0 when keyboard starts at 53)
        return Vector3.zero;
    }

    public bool IsWhiteKey(int keyIndex)
    {
        // FIX: Remove the +9 offset. 
        // Standard MIDI maps 0 to C-1. 
        // Therefore keyIndex % 12 aligns perfectly with C-major starting at 0.
        return keyIndex >= 0 && keyIndex <= 127 && chromaticPattern[keyIndex % 12];
    }

    public int NoteNameToKeyIndex(string note)
    {
        int number;
        return ARMusician.Midi.MidiNotes.TryParse(note, out number) ? number : -1;
    }
}
