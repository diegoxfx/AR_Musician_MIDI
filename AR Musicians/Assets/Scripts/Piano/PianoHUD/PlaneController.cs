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
    public int totalKeys = 32;       // Set to 32 for your hardware
    public string leftmostKey = "F3"; // Set to "F3"

    // Internal variable to store the parsed integer of the leftmost key
    private int leftmostKeyIndex = -1;

    public float whiteToBlackRatio = 1.66f;

    // Using a Dictionary is safer for sparse lookups, but we keep your variable names 
    // to minimize friction. 
    private Dictionary<int, Vector3> localKeyCenters = new Dictionary<int, Vector3>();
    private Dictionary<int, float> keyWidths = new Dictionary<int, float>();

    private static string[] noteNames =
            { "C", "C#", "D", "D#", "E", "F", "F#", "G", "G#", "A", "A#", "B" };

    // FIXED PATTERN: Starts at C, goes to B. (True = White, False = Black)
    // C, C#, D, D#, E, F, F#, G, G#, A, A#, B
    private static bool[] chromaticPattern =
            { true, false, true, false, true, true, false, true, false, true, false, true };

    void Start()
    {
        reInit();
    }

    // Keep this public as requested
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

        float whiteWidthUnit = 1f;
        float blackWidthUnit = whiteWidthUnit / whiteToBlackRatio;

        // 1. Count total width units required for the specific 32 keys
        float totalUnits = 0f;
        for (int i = 0; i < totalKeys; i++)
        {
            int currentMidiNote = leftmostKeyIndex + i;
            bool isWhite = IsWhiteKey(currentMidiNote);
            totalUnits += isWhite ? whiteWidthUnit : blackWidthUnit;
        }

        if (totalUnits <= 0) return; // Prevent division by zero

        float unitToLocal = 1f / totalUnits;
        float currentX = -0.5f;

        // 2. Compute positions
        for (int i = 0; i < totalKeys; i++)
        {
            int currentMidiNote = leftmostKeyIndex + i;

            bool isWhite = IsWhiteKey(currentMidiNote);
            float wUnits = isWhite ? whiteWidthUnit : blackWidthUnit;
            float worldWidth = wUnits * unitToLocal;

            float centerX = currentX + (worldWidth / 2f);

            // Store results keyed by the MIDI Note Number
            if (!localKeyCenters.ContainsKey(currentMidiNote))
            {
                localKeyCenters.Add(currentMidiNote, new Vector3(centerX, 0.5f, 0f));
                keyWidths.Add(currentMidiNote, worldWidth);
            }

            currentX += worldWidth;
        }
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
            return localKeyCenters[keyIndex];
        }

        // Return 0 if the note is not part of the keyboard (e.g. note 0 when keyboard starts at 53)
        return Vector3.zero;
    }

    public bool IsWhiteKey(int keyIndex)
    {
        // FIX: Remove the +9 offset. 
        // Standard MIDI maps 0 to C-1. 
        // Therefore keyIndex % 12 aligns perfectly with C-major starting at 0.
        return chromaticPattern[keyIndex % 12];
    }

    public int NoteNameToKeyIndex(string note)
    {
        if (string.IsNullOrEmpty(note)) return 0;

        try
        {
            string namePart = note.Substring(0, note.Length - 1);
            int octave = int.Parse(note.Substring(note.Length - 1));
            int noteNumber = System.Array.IndexOf(noteNames, namePart);

            // FIX: Standard MIDI logic.
            // C-1 is 0. C0 is 12. C4 is 60.
            // Formula: (Octave + 1) * 12 + NoteIndex
            // Note: If your MIDI files assume C0 = 0, remove the "+ 1". 
            // Standard DryWetMidi usage assumes C-1 = 0.
            int keyIndex = noteNumber + 12 * (octave + 1);

            return keyIndex;
        }
        catch
        {
            Debug.LogError($"Error parsing note name: {note}. Defaulting to 0.");
            return 0;
        }
    }
}