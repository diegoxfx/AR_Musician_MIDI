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

    private enum TransportStatus
    {
        Starting, Waiting, Connecting, Connected, VirtualConnected, Disconnected,
        Stopped, Interrupted, Reconnect, Unavailable, Unsupported, Unknown
    }

    private PianoStrategy piano;
    private RhythmGameManager game;
    private PianoManagerInstrumentDefiner definer;
    private bool wasDefining;
    private readonly HeldNotes held = new HeldNotes();
    private readonly PracticeMatcher matcher = new PracticeMatcher();
    private KeyboardCalibration calibration;
    private bool initialized, calibrating, rangeReady, enabledFeedback = true, scoring;
    public bool IsInitialized => initialized;
    public bool IsConnected => initialized && transportConnected;
    public bool IsCalibrated => initialized && rangeReady;
    public bool IsCalibrating => initialized && calibrating;
    public bool FeedbackEnabled => enabledFeedback;
    public string StatusMessage => CompactStatus();
    public bool SuppressStatusLabel
    {
        get => suppressStatusLabel;
        set
        {
            suppressStatusLabel = value;
            if (value && label != null) label.gameObject.SetActive(false);
        }
    }
    public int FirstNote => firstNote;
    public int LastNote => firstNote + keyCount - 1;
    private int firstNote = 36, keyCount = 61, skipped;
    private TransportStatus transport;
    private bool transportConnected;
    private bool suppressStatusLabel, resetDuringSession;
    private readonly Dictionary<int, GameObject> markers = new Dictionary<int, GameObject>();
    private readonly Dictionary<int, float> flashes = new Dictionary<int, float>();
    private Material markerMaterial;
    private MaterialPropertyBlock markerProperties;
    private TextMesh label;
    private AndroidJavaClass bridge;
    private const double TimingWindow = 0.30;

    public void Initialize(PianoStrategy strategy)
    {
        if (initialized) return;
        try
        {
            if (strategy == null || strategy.plane == null)
                throw new InvalidOperationException("Piano strategy or plane is missing");
            piano = strategy;
            game = FindFirstObjectByType<RhythmGameManager>();
            if (game == null) throw new InvalidOperationException("Rhythm game manager is missing");
            definer = FindFirstObjectByType<PianoManagerInstrumentDefiner>();
            if (piano.noteCubePrefab == null) throw new InvalidOperationException("NoteCube prefab is missing");
            var renderer = piano.noteCubePrefab.GetComponent<Renderer>();
            if (renderer == null) throw new InvalidOperationException("NoteCube prefab has no Renderer");
            if (renderer.sharedMaterial == null || renderer.sharedMaterial.shader == null)
                throw new InvalidOperationException("NoteCube prefab material or shader is missing");

            var font = Resources.GetBuiltinResource<Font>("LegacyRuntime.ttf");
            if (font == null || font.material == null)
                throw new InvalidOperationException("MIDI status font or font material is missing");
            var labelObject = new GameObject("USB MIDI status");
            labelObject.SetActive(false);
            labelObject.transform.SetParent(transform, false);
            label = labelObject.AddComponent<TextMesh>();
            var labelRenderer = label.GetComponent<MeshRenderer>();
            if (labelRenderer == null) throw new InvalidOperationException("MIDI status TextMesh has no MeshRenderer");
            label.font = font;
            labelRenderer.sharedMaterial = font.material;
            label.fontSize = 32;
            label.characterSize = 0.003f;
            label.anchor = TextAnchor.MiddleCenter;
            label.alignment = TextAlignment.Center;
            label.color = Color.white;
            // The serialized prefab material keeps this shader in the Android build.
            markerMaterial = new Material(renderer.sharedMaterial);
            markerMaterial.color = Color.green;
            markerProperties = new MaterialPropertyBlock();
            calibration = new KeyboardCalibration(keyCount);
            game.InputInterrupted += InterruptInput;
            initialized = true;
            StartInput();
        }
        catch (Exception e)
        {
            ReleaseResources();
            transport = TransportStatus.Unavailable;
            Debug.LogWarning("USB MIDI disabled: " + e.Message);
        }
    }

    private void StartInput()
    {
#if UNITY_ANDROID && !UNITY_EDITOR
        transportConnected = false;
        transport = TransportStatus.Starting;
        try
        {
            if (bridge == null) bridge = new AndroidJavaClass("com.armusicians.midi.QuestMidi");
            using (var player = new AndroidJavaClass("com.unity3d.player.UnityPlayer"))
            using (var activity = player.GetStatic<AndroidJavaObject>("currentActivity"))
                bridge.CallStatic("start", activity);
        }
        catch (Exception e)
        {
            transport = TransportStatus.Unavailable;
            Debug.LogWarning("USB MIDI unavailable: " + e.Message);
        }
#elif UNITY_EDITOR || UNITY_STANDALONE_WIN
        transportConnected = virtualInput;
        transport = virtualInput ? TransportStatus.VirtualConnected : TransportStatus.Unsupported;
#else
        transport = TransportStatus.Unsupported;
#endif
    }

    private void StopInput()
    {
        transportConnected = false;
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
        if (rangeReady && game != null && game.HasSession) resetDuringSession = true;
        rangeReady = false;
        if (game != null) game.OnPracticeInputReset();
    }

    private void OnApplicationPause(bool paused)
    {
        if (!initialized) return;
        // Unity does not guarantee callback ordering across components.
        if (game != null) game.SetApplicationPaused(paused);
        if (paused) StopInput();
        else StartInput();
    }

    private void OnApplicationFocus(bool focused)
    {
        if (!initialized) return;
        if (game != null) game.SetApplicationFocused(focused);
        if (!focused) InterruptInput();
    }

    private void InterruptInput()
    {
        held.Reset();
        flashes.Clear();
        calibrating = false;
    }

    private bool UpdateDefinitionState()
    {
        bool defining = definer != null && definer.IsDefining;
        if (defining && !wasDefining)
        {
            // Setup owns the controller buttons and pose. Preserve only a completed USB range.
            InterruptInput();
            scoring = false;
        }
        wasDefining = defining;
        return defining;
    }

    private void Update()
    {
        if (!initialized || piano == null || game == null) return;
        bool defining = UpdateDefinitionState();
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
            catch (Exception e)
            {
                transport = TransportStatus.Unavailable;
                Debug.LogWarning("USB MIDI error: " + e.Message);
                StopInput();
                bridge.Dispose();
                bridge = null;
            }
        }

        if (defining)
        {
            DrawMarkers(false);
            label.gameObject.SetActive(false);
            return;
        }

        bool isPiano = game.IsPiano && piano.HasCalibration;
        // Existing A button still opens the pause menu. MIDI setup is only allowed while idle.
        if (isPiano && game.CanSelectInstrument)
        {
            if (OVRInput.GetDown(OVRInput.Button.Two, OVRInput.Controller.RTouch)) RequestCalibration();
            if (OVRInput.GetDown(OVRInput.Button.One, OVRInput.Controller.LTouch)) ReverseKeyboardAlignment();
            if (OVRInput.GetDown(OVRInput.Button.Two, OVRInput.Controller.LTouch))
                SetFeedbackEnabled(!enabledFeedback);
        }
        if (scoring && !game.LearningMode && game.playing && game.InputAllowed && isPiano) matcher.Advance(game.SongTime, TimingWindow);
        DrawMarkers(isPiano && game.InputAllowed && rangeReady && enabledFeedback);
        bool showLabel = !SuppressStatusLabel && isPiano && game.InputAllowed &&
            (game.CanSelectInstrument || (game.playing && game.LearningMode && rangeReady));
        label.gameObject.SetActive(showLabel);
        if (showLabel)
        {
            label.text = StatusMessage;
            label.transform.position = piano.plane.transform.TransformPoint(new Vector3(0, 0.56f, 0));
            if (Camera.main != null) label.transform.rotation = Quaternion.LookRotation(label.transform.position - Camera.main.transform.position);
        }
    }

    private string CompactStatus()
    {
        if (calibrating) return AppText.Choose("MIDI calibration", "Calibración MIDI") + "\n" + CalibrationPrompt();
        if (!rangeReady)
        {
            if (resetDuringSession && game != null && game.HasSession)
                return AppText.Choose("MIDI reset · Exit the song and recalibrate", "MIDI reiniciado · Sal de la canción y vuelve a calibrar");
            return TransportMessage();
        }
        if (game != null && game.LearningMode && game.playing)
            return game.WaitingForLearningNotes ? AppText.Choose("Play ", "Toca ") + game.PendingNotes
                : AppText.Choose("Learning · ", "Aprendizaje · ") + game.LearningCompletedGroups + "/" + game.LearningTotalGroups;
        return enabledFeedback
            ? AppText.Choose("MIDI ready · B to recalibrate", "MIDI listo · B para recalibrar")
            : AppText.Choose("MIDI ready · indicators off (Y)", "MIDI listo · indicadores desactivados (Y)");
    }

    private string CalibrationPrompt()
    {
        string error = calibration.Error;
        if (error == "The note must be in the MIDI range 0 to 127.")
            return AppText.Choose("Use a note in MIDI range 0–127", "Usa una nota del rango MIDI 0–127");
        if (error == "The keyboard endpoints must be white keys.")
            return AppText.Choose("Use a white key at each end", "Usa una tecla blanca en cada extremo");
        if (error != null && error.StartsWith("The high note must be ", StringComparison.Ordinal))
            return AppText.Choose($"Expected {keyCount} keys. Try the rightmost key again.",
                $"Se necesitan {keyCount} teclas. Vuelve a tocar la tecla del extremo derecho.");
        if (error != null) return AppText.Choose("Try that key again", "Vuelve a tocar esa tecla");
        return calibration.FirstNote < 0
            ? AppText.Choose("Press and release the leftmost key", "Pulsa y suelta la tecla del extremo izquierdo")
            : AppText.Choose("Press the rightmost key", "Pulsa la tecla del extremo derecho");
    }

    private string TransportMessage()
    {
        switch (transport)
        {
            case TransportStatus.Starting: return AppText.Choose("Starting USB MIDI…", "Iniciando MIDI USB…");
            case TransportStatus.Waiting: return AppText.Choose("Connect the keyboard USB cable", "Conecta el cable USB del teclado");
            case TransportStatus.Connecting: return AppText.Choose("Connecting MIDI…", "Conectando MIDI…");
            case TransportStatus.Connected: return AppText.Choose("MIDI connected · B to calibrate", "MIDI conectado · B para calibrar");
            case TransportStatus.VirtualConnected: return AppText.Choose("Virtual MIDI connected · B to calibrate", "MIDI virtual conectado · B para calibrar");
            case TransportStatus.Disconnected: return AppText.Choose("MIDI disconnected", "MIDI desconectado");
            case TransportStatus.Stopped: return AppText.Choose("USB MIDI stopped", "MIDI USB detenido");
            case TransportStatus.Interrupted: return AppText.Choose("MIDI interrupted · Recalibrate", "MIDI interrumpido · Vuelve a calibrar");
            case TransportStatus.Reconnect: return AppText.Choose("Reconnect the MIDI keyboard", "Vuelve a conectar el teclado MIDI");
            case TransportStatus.Unavailable: return AppText.Choose("USB MIDI unavailable", "MIDI USB no disponible");
            case TransportStatus.Unsupported: return AppText.Choose("USB MIDI requires the Android player", "MIDI USB requiere la app para Android");
            default: return AppText.Choose("USB MIDI status unknown", "Estado de MIDI USB desconocido");
        }
    }

    private static TransportStatus ReadTransportStatus(string message)
    {
        if (string.IsNullOrEmpty(message)) return TransportStatus.Unknown;
        if (message.StartsWith("Connected USB MIDI:", StringComparison.Ordinal)) return TransportStatus.Connected;
        if (message.StartsWith("Opening USB MIDI:", StringComparison.Ordinal)) return TransportStatus.Connecting;
        if (message.StartsWith("Waiting for a USB MIDI keyboard", StringComparison.Ordinal)) return TransportStatus.Waiting;
        if (message.StartsWith("USB MIDI disconnected:", StringComparison.Ordinal)) return TransportStatus.Disconnected;
        if (message.StartsWith("USB MIDI stopped", StringComparison.Ordinal)) return TransportStatus.Stopped;
        if (message.StartsWith("USB MIDI queue overflow", StringComparison.Ordinal)) return TransportStatus.Interrupted;
        if (message.StartsWith("USB MIDI failed for ", StringComparison.Ordinal)) return TransportStatus.Reconnect;
        if (message.StartsWith("USB MIDI unavailable:", StringComparison.Ordinal) ||
            message.StartsWith("USB MIDI requires Android", StringComparison.Ordinal) ||
            message.StartsWith("Android MIDI service unavailable", StringComparison.Ordinal) ||
            message.StartsWith("USB MIDI setup failed:", StringComparison.Ordinal)) return TransportStatus.Unavailable;
        return TransportStatus.Unknown;
    }

    /// <summary>Shared guarded action for a menu button or a controller shortcut.</summary>
    public bool RequestCalibration()
    {
        if (!IsConnected || game == null || piano == null || !game.IsPiano ||
            !game.CanSelectInstrument || !piano.HasCalibration || UpdateDefinitionState()) return false;
        BeginCalibration();
        return calibrating;
    }

    public bool SetFeedbackEnabled(bool enabled)
    {
        if (!initialized || game == null || piano == null || !piano.HasCalibration ||
            !game.IsPiano || !game.CanSelectInstrument || UpdateDefinitionState()) return false;
        enabledFeedback = enabled;
        scoring = false;
        return true;
    }

    public bool ReverseKeyboardAlignment()
    {
        if (!initialized || game == null || piano == null || !game.IsPiano ||
            !game.CanSelectInstrument || !rangeReady || UpdateDefinitionState()) return false;
        piano.plane.reverseHorizontal = !piano.plane.reverseHorizontal;
        return true;
    }

#if UNITY_EDITOR || UNITY_STANDALONE_WIN
    private bool virtualInput;

    public void EnableVirtualInput()
    {
        virtualInput = true;
        transportConnected = true;
        transport = TransportStatus.VirtualConnected;
    }

    public void BeginVirtualCalibration()
    {
        if (virtualInput) RequestCalibration();
    }

    public void ReceiveVirtualNote(int pitch, bool on, int velocity = 96)
    {
        if (!virtualInput) return;
        Receive(new MidiEvent { type = "note", channel = 0, note = pitch, on = on, velocity = on ? velocity : 0 });
    }
#endif

    private void BeginCalibration()
    {
        if (!initialized || UpdateDefinitionState()) return;
        scoring = false;
        rangeReady = false;
        held.Reset();
        flashes.Clear();
        calibration = new KeyboardCalibration(keyCount);
        calibration.Begin();
        calibrating = true;
    }

    private void Receive(MidiEvent e)
    {
        if (!initialized || e == null) return;
        bool defining = UpdateDefinitionState();
        if (e.type == "status")
        {
            // Device names and bridge errors belong in diagnostics. The UI shows a short localized state.
            transport = ReadTransportStatus(e.message);
            if (transport == TransportStatus.Connected || transport == TransportStatus.VirtualConnected)
                transportConnected = true;
            else if (transport != TransportStatus.Interrupted && transport != TransportStatus.Unknown)
                transportConnected = false;
            // Queue overflow invalidates the note range, not the open device.
            // An unknown diagnostic must not create a connection that never existed.
            if (!transportConnected) calibrating = false;
            if (!string.IsNullOrEmpty(e.message)) Debug.Log(e.message);
            return;
        }
        if (e.type == "reset") { ResetInput(); return; }
        if (e.type == "channelReset")
        {
            held.ResetChannel(e.channel);
            game?.OnPracticeInputReset();
            return;
        }
        if (defining) return;
        if (game == null || !game.InputAllowed) return;
        if (e.type != "note" || e.note < 0 || e.note > 127 || e.channel < 0 || e.channel > 15) return;
        bool wasHeld = held.IsHeld(e.note);
        bool noteOn = e.on && e.velocity > 0;
        held.Set(e.channel, e.note, noteOn);
        if (!noteOn)
        {
            if (rangeReady && wasHeld && !held.IsHeld(e.note)) game.ReceivePracticeNote(e.note, false);
            return;
        }
        if (calibrating)
        {
            calibration.Observe(e.note);
            if (calibration.Error != null) Debug.Log(calibration.Error);
            if (calibration.IsComplete)
            {
                firstNote = calibration.FirstNote;
                rangeReady = true;
                calibrating = false;
                piano.plane.totalKeys = keyCount;
                piano.plane.leftmostKey = MidiNotes.Format(firstNote);
                piano.plane.reInit();
                RebuildMarkers();
            }
            return;
        }
        if (!rangeReady || !game.IsPiano) return;
        if (!wasHeld) game.ReceivePracticeNote(e.note, true);
        if (!enabledFeedback) return;
        bool hit = scoring && !game.LearningMode && game.playing && matcher.Hit(e.note, game.SongTime, TimingWindow);
        flashes[e.note] = hit ? Time.unscaledTime + 0.25f : -Time.unscaledTime - 0.25f;
    }

    public void BeginSong(List<NoteEvent> song, float speed)
    {
        if (!initialized || UpdateDefinitionState()) return;
        calibrating = false;
        resetDuringSession = false;
        matcher.Clear();
        skipped = 0;
        scoring = enabledFeedback && rangeReady && !game.LearningMode;
        if (!scoring) return;
        foreach (var n in song)
        {
            if (n.keyIndex < firstNote || n.keyIndex >= firstNote + keyCount) { skipped++; continue; }
            matcher.Add(n.keyIndex, n.time / speed);
        }
    }

    public void EndSong(bool completed)
    {
        if (!initialized || UpdateDefinitionState()) return;
        if (completed && scoring) matcher.Advance(double.MaxValue, TimingWindow);
        scoring = false;
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
        ReleaseResources(false);
    }

    private void ReleaseResources(bool notifyGame = true)
    {
        initialized = false;
        if (game != null) game.InputInterrupted -= InterruptInput;
        // Component destruction is not a live device disconnect. Its owning
        // scene/menu may already be gone, so do not request recovery UI.
        if (!notifyGame) game = null;
        StopInput();
        if (bridge != null) bridge.Dispose();
        bridge = null;
        matcher.Clear();
        foreach (var marker in markers.Values) if (marker != null) Destroy(marker);
        if (markerMaterial != null) Destroy(markerMaterial);
        if (label != null) Destroy(label.gameObject);
        markers.Clear();
        markerMaterial = null;
        markerProperties = null;
        calibration = null;
        label = null;
        game = null;
        piano = null;
        definer = null;
        wasDefining = false;
    }
}
