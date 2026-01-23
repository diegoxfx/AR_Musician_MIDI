using System;
using System.Collections;
using UnityEngine;

public class BongoStrategy : MonoBehaviour, IGameplayStrategy
{
    [Header("Bongo Settings")]
    public BongosManager BongosManager; // Holds the DefinedCircle data
    public GameObject noteSpherePrefab;
    public float approachTime = 1.5f;
    public float spawnDistance = 0.8f;

    // Tracks the drums defined in BongosManager
    private DefinedCircle? leftDrum;
    private DefinedCircle? rightDrum;

    [SerializeField]
    private SpriteRenderer leftCircle;

    [SerializeField]
    private SpriteRenderer rightCircle;
    private static string[] noteNames =
                { "C", "C#", "D", "D#", "E", "F", "F#", "G", "G#", "A", "A#", "B" }; // used to map note names to key indices
    public event Action OnNoteFinished;

    public int average_key = 0;

    private void Start()
    {
        BongosManager.OnBongoDefined += HandleBongoDefined;
    }

    private void OnDestroy()
    {
        BongosManager.OnBongoDefined -= HandleBongoDefined;
    }

    private void HandleBongoDefined(DefinedCircle circle, BongosManager.SetupPhase phase)
    {
        if (phase == BongosManager.SetupPhase.DefiningLeftHembra)
        {
            leftDrum = circle;
            leftCircle.transform.localScale = new Vector3(circle.Radius * 2f, circle.Radius * 2f, 1f);
            leftCircle.transform.position = circle.Center;
        }
        else if (phase == BongosManager.SetupPhase.DefiningRightMacho)
        {
            rightDrum = circle;
            rightCircle.transform.localScale = new Vector3(circle.Radius * 2f, circle.Radius * 2f, 1f);
            rightCircle.transform.position = circle.Center;
        }
    }

    // Interface Implementation
    public float ApproachTime => approachTime;

    public bool IsInstrumentReady()
    {
        return leftDrum.HasValue && rightDrum.HasValue;
    }

    public void PreprocessNote(NoteEvent note)
    {
        // Map Keys to Drums (Simple split)
        // You can make this smarter (e.g., specific track names in MIDI)
        note.keyIndex = NoteNameToMidi(note.key); // 0 = Left, 1 = Right
    }

    public void SpawnNote(NoteEvent note, float gameSpeed)
    {
        bool isLeft = note.keyIndex < average_key;
        DefinedCircle targetDrum = isLeft ? leftDrum.Value : rightDrum.Value;

        GameObject noteObj = Instantiate(noteSpherePrefab);

        noteObj.transform.position = targetDrum.Center;

        // Orient the object so its "Up" matches the Drum Normal.
        // This ensures the X/Z drawing plane lies flat on the drum surface.
        if (targetDrum.Normal != Vector3.zero)
        {
            noteObj.transform.rotation = Quaternion.LookRotation(Vector3.forward, targetDrum.Normal);
        }

        Color c = isLeft ? Color.cyan : Color.magenta;
        var arcBehavior = noteObj.GetComponent<BongoArcBehavior>();
        if (arcBehavior == null) arcBehavior = noteObj.AddComponent<BongoArcBehavior>();

        float drumRadius = targetDrum.Radius;

        arcBehavior.Configure(this, isLeft, drumRadius, spawnDistance, approachTime, c);

        // Events
        RhythmGameManager.OnPause += arcBehavior.OnPause;
        RhythmGameManager.OnResume += arcBehavior.OnResume;
        RhythmGameManager.OnQuit += arcBehavior.OnQuit;
    }



    public void NotifyNoteDone()
    {
        OnNoteFinished?.Invoke();
    }
    public void NotifyNoteDone(bool isLeftDrum)
    {
        OnNoteFinished?.Invoke();
        return;
        if (isLeftDrum)
            StartCoroutine(ShowCircle(leftCircle));
        else
            StartCoroutine(ShowCircle(rightCircle));

    }


    IEnumerator ShowCircle(SpriteRenderer circleRenderer)
    {
        circleRenderer.enabled = true;

        // Wait for one rendered frame
        yield return new WaitForSeconds(0.05f);

        circleRenderer.enabled = false;
    }

    private int NoteNameToMidi(string note)
    {
        string namePart = note.Substring(0, note.Length - 1); // everything but the last character is part of the name
        int octave = int.Parse(note.Substring(note.Length - 1)); // gives the octave

        int noteNumber = System.Array.IndexOf(noteNames, namePart);

        int keyIndex = noteNumber + 12 * octave;
        return keyIndex;
    }
}