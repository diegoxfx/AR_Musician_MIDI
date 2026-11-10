using System;
using System.Collections.Generic;
using ARMusician.Midi;
using UnityEngine;

/// <summary>USB input is optional. Vision owns pose; MIDI owns pitch. No scene wiring required.</summary>
public sealed class PianoMidiFeedback : MonoBehaviour
{
    [Serializable] private class MidiEvent
    {
        public string type, message;
        public int channel, note, velocity;
        public bool on;
    }
    [Serializable] private class EventBatch { public MidiEvent[] events; }

    private PianoStrategy piano;
    private RhythmGameManager game;
    private readonly HeldNotes held = new HeldNotes();
    private readonly PracticeMatcher matcher = new PracticeMatcher();
    private KeyboardCalibration calibration;
    private bool calibrating, rangeReady, enabledFeedback = true, scoring;
    private int firstNote = 36, keyCount = 61, skipped;
    private string status = "USB MIDI: starting", instruction = "B: MIDI calibration (61 keys)";
    private readonly Dictionary<int, GameObject> markers = new Dictionary<int, GameObject>();
    private readonly Dictionary<int, float> flashes = new Dictionary<int, float>();
    private Material markerMaterial;
    private MaterialPropertyBlock markerProperties;
    private TextMesh label;
    private AndroidJavaClass bridge;
    private const double TimingWindow = 0.30;

    public void Initialize(PianoStrategy strategy)
    {
        piano = strategy;
        game = FindFirstObjectByType<RhythmGameManager>();
        if (game != null) game.InputInterrupted += InterruptInput;
        var labelObject = new GameObject("USB MIDI status");
        labelObject.transform.SetParent(transform, false);
        label = labelObject.AddComponent<TextMesh>();
        label.font = Resources.GetBuiltinResource<Font>("LegacyRuntime.ttf");
        label.GetComponent<MeshRenderer>().sharedMaterial = label.font.material;
        label.fontSize = 48;
        label.characterSize = 0.012f;
        label.anchor = TextAnchor.MiddleCenter;
        label.alignment = TextAlignment.Center;
        label.color = Color.white;
        // Use the scene's existing material so the Android build retains its shader.
        markerMaterial = new Material(piano.noteCubePrefab.GetComponent<Renderer>().sharedMaterial);
        markerMaterial.color = Color.green;
        markerProperties = new MaterialPropertyBlock();
        calibration = new KeyboardCalibration(keyCount);
        StartInput();
    }

    private void StartInput()
    {
#if UNITY_ANDROID && !UNITY_EDITOR
        try
        {
            if (bridge == null) bridge = new AndroidJavaClass("com.armusicians.midi.QuestMidi");
            using (var player = new AndroidJavaClass("com.unity3d.player.UnityPlayer"))
            using (var activity = player.GetStatic<AndroidJavaObject>("currentActivity"))
                bridge.CallStatic("start", activity);
        }
        catch (Exception e) { status = "USB MIDI unavailable: " + e.Message; }
#else
        status = "USB MIDI requires the Android player";
#endif
    }

    private void StopInput()
    {
        if (bridge != null)
        {
            try { bridge.CallStatic("stop"); }
            catch (Exception e) { Debug.LogWarning(e.Message); }
        }
        ResetInput();
    }

    private void ResetInput()
    {
        held.Reset();
        flashes.Clear();
        scoring = false;
        calibrating = false;
        rangeReady = false;
        instruction = game != null && game.HasSession
            ? "MIDI reset: quit the song, press B to recalibrate, then restart"
            : "B: calibrate MIDI again (61 keys)";
    }

    private void OnApplicationPause(bool paused)
    {
        if (piano == null) return;
        // Unity does not guarantee callback ordering across components.
        if (game != null) game.SetApplicationPaused(paused);
        if (paused) StopInput();
        else StartInput();
    }

    private void OnApplicationFocus(bool focused)
    {
        if (game != null) game.SetApplicationFocused(focused);
        if (!focused) InterruptInput();
    }

    private void InterruptInput()
    {
        held.Reset();
        flashes.Clear();
        calibrating = false;
        if (!rangeReady) instruction = "B: calibrate MIDI again (61 keys)";
    }

    private void Update()
    {
        if (piano == null || game == null) return;
        if (bridge != null)
        {
            try
            {
                string json = bridge.CallStatic<string>("poll");
                if (!string.IsNullOrEmpty(json))
                {
                    var batch = JsonUtility.FromJson<EventBatch>("{\"events\":" + json + "}");
                    if (batch != null && batch.events != null)
                        foreach (var e in batch.events) Receive(e);
                }
            }
            catch (Exception e) { status = "USB MIDI error: " + e.Message; StopInput(); bridge.Dispose(); bridge = null; }
        }

        bool isPiano = game.IsPiano && piano.HasCalibration;
        // Existing A button still opens the pause menu. MIDI setup is only allowed while idle.
        if (isPiano && game.CanSelectInstrument)
        {
            if (OVRInput.GetDown(OVRInput.Button.Two, OVRInput.Controller.RTouch)) BeginCalibration();
            if (OVRInput.GetDown(OVRInput.Button.One, OVRInput.Controller.LTouch) && rangeReady)
            { piano.plane.reverseHorizontal = !piano.plane.reverseHorizontal; instruction = "Check low, middle and high keys; X reverses alignment"; }
            if (OVRInput.GetDown(OVRInput.Button.Two, OVRInput.Controller.LTouch))
            { enabledFeedback = !enabledFeedback; scoring = false; }
        }
        if (scoring && game.playing && game.InputAllowed && isPiano) matcher.Advance(game.SongTime, TimingWindow);
        DrawMarkers(isPiano && game.InputAllowed && rangeReady && enabledFeedback);
        label.gameObject.SetActive(isPiano);
        if (isPiano)
        {
            string result = "Hits " + matcher.Hits + "  Misses " + matcher.Misses + "  Extra " + matcher.Wrong;
            label.text = status + "\n" + instruction + "\n" +
                (enabledFeedback ? result + "  Out of range " + skipped : "Feedback off") +
                "\nB: calibrate  X: reverse keys  Y: feedback on/off";
            label.transform.position = piano.plane.transform.TransformPoint(new Vector3(0, 0.72f, 0));
            if (Camera.main != null) label.transform.rotation = Quaternion.LookRotation(label.transform.position - Camera.main.transform.position);
        }
    }

    private void BeginCalibration()
    {
        scoring = false;
        rangeReady = false;
        held.Reset();
        flashes.Clear();
        calibration = new KeyboardCalibration(keyCount);
        calibration.Begin();
        calibrating = true;
        instruction = "Press and release the LEFTMOST key, then the RIGHTMOST key";
    }

    private void Receive(MidiEvent e)
    {
        if (e.type == "status") { status = e.message; return; }
        if (e.type == "reset") { ResetInput(); return; }
        if (e.type == "channelReset") { held.ResetChannel(e.channel); return; }
        if (game == null || !game.InputAllowed) return;
        if (e.type != "note" || e.note < 0 || e.note > 127 || e.channel < 0 || e.channel > 15) return;
        held.Set(e.channel, e.note, e.on && e.velocity > 0);
        if (!e.on || e.velocity == 0) return;
        if (calibrating)
        {
            calibration.Observe(e.note);
            instruction = calibration.Error ?? "Now press the RIGHTMOST key";
            if (calibration.IsComplete)
            {
                firstNote = calibration.FirstNote;
                rangeReady = true;
                calibrating = false;
                piano.plane.totalKeys = keyCount;
                piano.plane.leftmostKey = MidiNotes.Format(firstNote);
                piano.plane.reInit();
                RebuildMarkers();
                instruction = "MIDI " + firstNote + "–" + calibration.LastNote + ": check low, middle and high keys";
            }
            return;
        }
        if (!rangeReady || !enabledFeedback || !game.IsPiano) return;
        bool hit = scoring && game.playing && matcher.Hit(e.note, game.SongTime, TimingWindow);
        flashes[e.note] = hit ? Time.unscaledTime + 0.25f : -Time.unscaledTime - 0.25f;
    }

    public void BeginSong(List<NoteEvent> song, float speed)
    {
        calibrating = false;
        matcher.Clear();
        skipped = 0;
        scoring = enabledFeedback && rangeReady;
        if (!scoring) return;
        foreach (var n in song)
        {
            if (n.keyIndex < firstNote || n.keyIndex >= firstNote + keyCount) { skipped++; continue; }
            matcher.Add(n.keyIndex, n.time / speed);
        }
    }

    public void EndSong(bool completed)
    {
        if (completed && scoring) matcher.Advance(double.MaxValue, TimingWindow);
        scoring = false;
        if (!rangeReady) instruction = "B: calibrate MIDI again (61 keys)";
    }

    private void RebuildMarkers()
    {
        foreach (var marker in markers.Values) Destroy(marker);
        markers.Clear();
        for (int n = firstNote; n < firstNote + keyCount; n++)
        {
            var marker = GameObject.CreatePrimitive(PrimitiveType.Cube);
            marker.name = "MIDI " + n;
            Destroy(marker.GetComponent<Collider>());
            marker.transform.SetParent(piano.plane.transform, false);
            marker.GetComponent<Renderer>().sharedMaterial = markerMaterial;
            marker.SetActive(false);
            markers[n] = marker;
        }
    }

    private void DrawMarkers(bool visible)
    {
        foreach (var pair in markers)
        {
            float flash;
            bool flashing = flashes.TryGetValue(pair.Key, out flash) && Mathf.Abs(flash) > Time.unscaledTime;
            bool active = visible && (held.IsHeld(pair.Key) || flashing);
            pair.Value.SetActive(active);
            if (!active) continue;
            var position = piano.plane.GetLocalKeyPosition(pair.Key);
            position.y = -0.5f; // Same strike line as falling notes, not the top of the waterfall.
            position.z = -0.004f;
            pair.Value.transform.localPosition = position;
            pair.Value.transform.localScale = new Vector3(piano.plane.GetLocalKeyWidth(pair.Key), 0.025f, 0.008f);
            var color = flashing && flash < 0 && scoring && game.playing ? Color.red : Color.green;
            markerProperties.SetColor("_Color", color);
            markerProperties.SetColor("_BaseColor", color);
            pair.Value.GetComponent<Renderer>().SetPropertyBlock(markerProperties);
        }
    }

    private void OnDestroy()
    {
        if (game != null) game.InputInterrupted -= InterruptInput;
        StopInput();
        if (bridge != null) bridge.Dispose();
        foreach (var marker in markers.Values) if (marker != null) Destroy(marker);
        if (markerMaterial != null) Destroy(markerMaterial);
        if (label != null) Destroy(label.gameObject);
    }
}
