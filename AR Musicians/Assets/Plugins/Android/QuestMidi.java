package com.armusicians.midi;

import android.content.Context;
import android.media.midi.MidiDevice;
import android.media.midi.MidiDeviceInfo;
import android.media.midi.MidiManager;
import android.media.midi.MidiOutputPort;
import android.media.midi.MidiReceiver;
import android.os.Build;
import android.os.Bundle;
import android.os.Handler;
import android.os.Looper;

import java.io.Closeable;
import java.io.IOException;
import java.util.ArrayDeque;
import java.util.ArrayList;
import java.util.Collections;
import java.util.Comparator;
import java.util.HashMap;
import java.util.Locale;

/** Unity polls this bridge; no Unity classes or third-party MIDI libraries are required. */
public final class QuestMidi {
    private static final Object LOCK = new Object();
    private static final int MAX_EVENTS = 512;
    private static final ArrayDeque<String> EVENTS = new ArrayDeque<String>();
    private static final Handler MAIN = new Handler(Looper.getMainLooper());
    // All fields below are guarded by LOCK, or confined to MAIN as indicated.
    private static boolean requested;
    private static long runId;
    private static long connectionId;

    // Android device discovery/open/close is serialized on MAIN. MIDI arrives on binder threads.
    private static MidiManager manager;
    private static MidiManager.DeviceCallback callback;
    private static final HashMap<Integer, MidiDeviceInfo> devices =
            new HashMap<Integer, MidiDeviceInfo>();
    private static int selectedId = -1;
    private static MidiDevice device;
    private static MidiOutputPort output;
    private static MidiReceiver receiver;

    private QuestMidi() { }

    /** Idempotent until stop(). Pass an Activity or its application context. */
    public static void start(Context context) {
        if (context == null) {
            status("USB MIDI unavailable: Android context is null.");
            return;
        }
        final Context appContext = context.getApplicationContext();
        final long run;
        synchronized (LOCK) {
            if (requested) return;
            requested = true;
            run = ++runId;
            resetQueueLocked();
            MAIN.post(new Runnable() {
                @Override public void run() { startOnMain(appContext, run); }
            });
        }
    }

    /** Invalidates incoming callbacks immediately; resources close on Android's main thread. */
    public static void stop() {
        synchronized (LOCK) {
            if (!requested) return;
            requested = false;
            ++runId;
            ++connectionId;
            resetQueueLocked();
            enqueueLocked("{\"type\":\"status\",\"message\":\"USB MIDI stopped.\"}");
            MAIN.post(new Runnable() {
                @Override public void run() { teardownOnMain(); }
            });
        }
    }

    /** Bounded JSON array, drained atomically. Must be called regularly by Unity Update(). */
    public static String poll() {
        synchronized (LOCK) {
            StringBuilder json = new StringBuilder("[");
            while (!EVENTS.isEmpty()) {
                if (json.length() > 1) json.append(',');
                json.append(EVENTS.removeFirst());
            }
            return json.append(']').toString();
        }
    }

    private static boolean current(long run) {
        synchronized (LOCK) { return requested && runId == run; }
    }

    @SuppressWarnings("deprecation") // API 23 byte-stream API also works on newer Android.
    private static void startOnMain(final Context context, final long run) {
        if (!current(run)) return;
        teardownOnMain();
        if (Build.VERSION.SDK_INT < 23) {
            status("USB MIDI requires Android 6.0 or later.");
            return;
        }
        try {
            manager = (MidiManager) context.getSystemService(Context.MIDI_SERVICE);
            if (manager == null) {
                status("Android MIDI service unavailable on this headset.");
                return;
            }
            // Populate first, then register. The second snapshot closes the discovery race.
            for (MidiDeviceInfo info : manager.getDevices()) addDevice(info);
            callback = new MidiManager.DeviceCallback() {
                @Override public void onDeviceAdded(MidiDeviceInfo info) {
                    if (!current(run)) return;
                    addDevice(info);
                    chooseDevice(run);
                }
                @Override public void onDeviceRemoved(MidiDeviceInfo info) {
                    if (!current(run)) return;
                    devices.remove(info.getId());
                    if (selectedId == info.getId()) {
                        closeConnection();
                        status("USB MIDI disconnected: " + nameOf(info));
                    }
                    chooseDevice(run);
                }
            };
            manager.registerDeviceCallback(callback, MAIN);
            devices.clear();
            for (MidiDeviceInfo info : manager.getDevices()) addDevice(info);
            chooseDevice(run);
        } catch (RuntimeException error) {
            teardownOnMain();
            status("USB MIDI setup failed: " + error.getClass().getSimpleName());
        }
    }

    private static void addDevice(MidiDeviceInfo info) {
        if (info.getType() == MidiDeviceInfo.TYPE_USB && info.getOutputPortCount() > 0) {
            devices.put(info.getId(), info);
        }
    }

    private static void chooseDevice(final long run) {
        if (!current(run) || selectedId != -1) return; // Keep the current keyboard stable.
        ArrayList<MidiDeviceInfo> candidates = new ArrayList<MidiDeviceInfo>(devices.values());
        Collections.sort(candidates, new Comparator<MidiDeviceInfo>() {
            @Override public int compare(MidiDeviceInfo left, MidiDeviceInfo right) {
                int preference = (isCasio(left) ? 0 : 1) - (isCasio(right) ? 0 : 1);
                return preference != 0 ? preference : Integer.compare(left.getId(), right.getId());
            }
        });
        if (candidates.isEmpty()) {
            status("Waiting for a USB MIDI keyboard. Connect its USB data cable to the headset.");
            return;
        }
        final MidiDeviceInfo info = candidates.get(0);
        selectedId = info.getId();
        final long connection;
        synchronized (LOCK) { connection = ++connectionId; }
        status("Opening USB MIDI: " + nameOf(info));
        try {
            manager.openDevice(info, new MidiManager.OnDeviceOpenedListener() {
                @Override public void onDeviceOpened(MidiDevice opened) {
                    synchronized (LOCK) {
                        if (!requested || runId != run || connectionId != connection) {
                            closeQuietly(opened);
                            return;
                        }
                    }
                    if (opened == null) {
                        connectionFailed(info, "Android could not open the device.");
                        return;
                    }
                    device = opened;
                    try {
                        // Port direction is from the device's viewpoint: OUTPUT sends to us.
                        int portNumber = firstOutputPort(info);
                        output = opened.openOutputPort(portNumber);
                        if (output == null) {
                            connectionFailed(info, "Output port is unavailable.");
                            return;
                        }
                        final MidiStreamDecoder decoder = new MidiStreamDecoder(new MidiStreamDecoder.Sink() {
                            @Override public void note(int channel, int note, int velocity, boolean on) {
                                enqueueLocked("{\"type\":\"note\",\"channel\":" + channel
                                        + ",\"note\":" + note + ",\"velocity\":" + velocity
                                        + ",\"on\":" + on + "}");
                            }
                            @Override public void channelReset(int channel) {
                                enqueueLocked("{\"type\":\"channelReset\",\"channel\":" + channel + "}");
                            }
                            @Override public void reset() { enqueueLocked("{\"type\":\"reset\"}"); }
                        });
                        receiver = new MidiReceiver() {
                            @Override public void onSend(byte[] data, int offset, int count, long timestamp) {
                                synchronized (LOCK) {
                                    if (requested && runId == run && connectionId == connection) {
                                        decoder.send(data, offset, count);
                                    }
                                }
                            }
                            @Override public void onFlush() {
                                synchronized (LOCK) {
                                    if (requested && runId == run && connectionId == connection) {
                                        decoder.clear();
                                        enqueueLocked("{\"type\":\"reset\"}");
                                    }
                                }
                            }
                        };
                        output.connect(receiver);
                        status("Connected USB MIDI: " + nameOf(info) + " (output port " + portNumber + ").");
                    } catch (RuntimeException error) {
                        connectionFailed(info, error.getClass().getSimpleName());
                    }
                }
            }, MAIN);
        } catch (RuntimeException error) {
            connectionFailed(info, error.getClass().getSimpleName());
        }
    }

    private static int firstOutputPort(MidiDeviceInfo info) {
        int first = Integer.MAX_VALUE;
        for (MidiDeviceInfo.PortInfo port : info.getPorts()) {
            if (port.getType() == MidiDeviceInfo.PortInfo.TYPE_OUTPUT) {
                first = Math.min(first, port.getPortNumber());
            }
        }
        if (first == Integer.MAX_VALUE) throw new IllegalStateException("No output port");
        return first;
    }

    private static void connectionFailed(MidiDeviceInfo info, String reason) {
        closeConnection();
        // Do not silently select another instrument after an open failure. Replug or resume retries.
        selectedId = info.getId();
        status("USB MIDI failed for " + nameOf(info) + ": " + reason + " Reconnect the keyboard to retry.");
    }

    private static void closeConnection() {
        synchronized (LOCK) {
            ++connectionId;
            enqueueLocked("{\"type\":\"reset\"}");
        }
        if (output != null && receiver != null) {
            try { output.disconnect(receiver); } catch (RuntimeException ignored) { }
        }
        closeQuietly(output);
        closeQuietly(device);
        output = null;
        device = null;
        receiver = null;
        selectedId = -1;
    }

    private static void teardownOnMain() {
        if (manager != null && callback != null) {
            try { manager.unregisterDeviceCallback(callback); } catch (RuntimeException ignored) { }
        }
        callback = null;
        closeConnection();
        devices.clear();
        manager = null;
    }

    private static void closeQuietly(Closeable resource) {
        if (resource == null) return;
        try { resource.close(); } catch (IOException ignored) { } catch (RuntimeException ignored) { }
    }

    private static String nameOf(MidiDeviceInfo info) {
        Bundle properties = info.getProperties();
        String name = properties.getString(MidiDeviceInfo.PROPERTY_NAME);
        if (name == null || name.trim().length() == 0) {
            String manufacturer = properties.getString(MidiDeviceInfo.PROPERTY_MANUFACTURER, "");
            String product = properties.getString(MidiDeviceInfo.PROPERTY_PRODUCT, "");
            name = (manufacturer + " " + product).trim();
        }
        if (name.length() == 0) name = "USB MIDI device " + info.getId();
        // Device labels are untrusted strings and should not produce unbounded status events.
        return name.substring(0, Math.min(name.length(), 160));
    }

    private static boolean isCasio(MidiDeviceInfo info) {
        String manufacturer = info.getProperties().getString(MidiDeviceInfo.PROPERTY_MANUFACTURER, "");
        return (manufacturer + " " + nameOf(info)).toLowerCase(Locale.ROOT).contains("casio");
    }

    private static void status(String message) {
        synchronized (LOCK) {
            enqueueLocked("{\"type\":\"status\",\"message\":" + quote(message) + "}");
        }
    }

    private static void resetQueueLocked() {
        EVENTS.clear();
        EVENTS.addLast("{\"type\":\"reset\"}");
    }

    private static void enqueueLocked(String event) {
        if (EVENTS.size() >= MAX_EVENTS) {
            // Dropping a note-off must never leave a held note in Unity.
            resetQueueLocked();
            EVENTS.addLast("{\"type\":\"status\",\"message\":\"USB MIDI queue overflow; held notes cleared.\"}");
        }
        EVENTS.addLast(event);
    }

    private static String quote(String value) {
        StringBuilder escaped = new StringBuilder("\"");
        for (int i = 0; i < value.length(); i++) {
            char c = value.charAt(i);
            switch (c) {
                case '\\': escaped.append("\\\\"); break;
                case '"': escaped.append("\\\""); break;
                case '\n': escaped.append("\\n"); break;
                case '\r': escaped.append("\\r"); break;
                case '\t': escaped.append("\\t"); break;
                default:
                    if (c < 0x20) escaped.append(String.format(Locale.ROOT, "\\u%04x", (int) c));
                    else escaped.append(c);
            }
        }
        return escaped.append('"').toString();
    }
}
