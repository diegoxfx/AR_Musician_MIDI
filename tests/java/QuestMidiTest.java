package com.armusicians.midi;

import android.content.Context;
import android.media.midi.MidiDevice;
import android.media.midi.MidiDeviceInfo;
import android.media.midi.MidiManager;
import android.media.midi.MidiReceiver;
import android.os.Handler;

/** Simulated Android callbacks test lifecycle and queue invariants, without claiming hardware QA. */
public final class QuestMidiTest {
    private static void check(boolean condition, String message) {
        if (!condition) throw new AssertionError(message);
    }
    private static MidiDeviceInfo keyboard(int id, String name, String maker, int... ports) {
        return new MidiDeviceInfo(id, MidiDeviceInfo.TYPE_USB, name, maker, ports);
    }
    private static void stop() { QuestMidi.stop(); Handler.drain(); QuestMidi.poll(); }
    public static void main(String[] args) {
        MidiManager manager = new MidiManager();
        MidiDeviceInfo generic = keyboard(1, "Keyboard", "Other", 0);
        MidiDeviceInfo casio = keyboard(9, "CT-\"S200\"\n", "CASIO", 3, 1);
        manager.devices.add(generic);
        manager.devices.add(casio);
        manager.devices.add(new MidiDeviceInfo(0, 2, "Bluetooth", "CASIO", 0));
        manager.devices.add(keyboard(2, "Input only", "CASIO"));
        Context context = new Context(manager);
        QuestMidi.start(context);
        QuestMidi.start(context);
        Handler.drain();
        check(manager.registrations == 1 && manager.opens.size() == 1, "Start must be idempotent");
        check(manager.opens.get(0).info == casio, "Prefer eligible USB Casio over generic/Bluetooth/input-only");
        MidiDevice connected = new MidiDevice();
        manager.completeOpen(0, connected);
        Handler.drain();
        check(connected.openedPort == 1, "Select lowest actual output port number");
        String status = QuestMidi.poll();
        check(status.contains("CT-\\\"S200\\\"\\n"), "Escape device labels as JSON");
        connected.output.send(0x92, 60, 100, 60, 0, 0xb2, 123, 0);
        check(QuestMidi.poll().equals("[{\"type\":\"note\",\"channel\":2,\"note\":60,\"velocity\":100,\"on\":true},"
                + "{\"type\":\"note\",\"channel\":2,\"note\":60,\"velocity\":0,\"on\":false},"
                + "{\"type\":\"channelReset\",\"channel\":2}]"), "Correct polled note/reset wire format");
        check(QuestMidi.poll().equals("[]"), "Poll drains events");
        for (int i = 0; i < 1200; i++) connected.output.send(0x90, i % 128, 100);
        String overflow = QuestMidi.poll();
        check(overflow.startsWith("[{\"type\":\"reset\"}"), "Overflow reset precedes retained notes");
        check(overflow.contains("queue overflow"), "Overflow is visible");
        check(overflow.split("\\},\\{").length <= 512, "Queue remains bounded");

        // A newly attached keyboard never steals an already selected instrument.
        manager.add(keyboard(3, "Other Casio", "Casio", 0)); Handler.drain();
        check(manager.opens.size() == 1, "Keep active keyboard stable");
        MidiReceiver staleReceiver = connected.output.receiver;
        manager.remove(casio); Handler.drain();
        check(connected.closed && connected.output.closed, "Disconnect closes resources");
        check(manager.opens.size() == 2 && manager.opens.get(1).info.getId() == 3, "Select remaining preferred USB device");
        QuestMidi.poll();
        staleReceiver.onSend(new byte[] {(byte) 0x90, 60, 100}, 0, 3, 0);
        check(QuestMidi.poll().equals("[]"), "Removed receiver cannot resurrect notes");

        // An asynchronous result from a removed device must not attach after replacement.
        MidiDeviceInfo replacement = manager.opens.get(1).info;
        manager.remove(replacement); Handler.drain();
        MidiDevice staleOpen = new MidiDevice();
        manager.completeOpen(1, staleOpen); Handler.drain();
        check(staleOpen.closed && staleOpen.output.receiver == null, "Removed device late open closes immediately");
        check(manager.opens.size() == 3 && manager.opens.get(2).info == generic, "Fallback selection is deterministic");

        // Stop/start reuses neither receiver nor pending device-open callback.
        QuestMidi.stop();
        QuestMidi.start(context);
        Handler.drain();
        MidiDevice stoppedOpen = new MidiDevice();
        manager.completeOpen(2, stoppedOpen); Handler.drain();
        check(stoppedOpen.closed, "Previous run late open closes immediately");
        check(manager.opens.size() == 4, "Resume starts a fresh open");
        MidiDevice resumed = new MidiDevice();
        manager.completeOpen(3, resumed); Handler.drain(); QuestMidi.poll();
        resumed.output.send(0x90, 65, 90);
        QuestMidi.stop();
        staleReceiver = resumed.output.receiver;
        staleReceiver.onSend(new byte[] {(byte) 0x90, 66, 90}, 0, 3, 0);
        String stopped = QuestMidi.poll();
        check(stopped.startsWith("[{\"type\":\"reset\"}") && !stopped.contains("\"note\""), "Stop invalidates receiver immediately and clears queued notes");
        Handler.drain();
        check(resumed.closed && manager.callback == null, "Stop releases device and discovery callback");
        stop();

        // Clear user-facing failure state and permit an explicit stop/start retry.
        MidiManager failure = new MidiManager();
        failure.devices.add(generic);
        QuestMidi.start(new Context(failure)); Handler.drain();
        MidiDevice badPort = new MidiDevice(); badPort.failPort = true;
        failure.completeOpen(0, badPort); Handler.drain();
        check(badPort.closed && QuestMidi.poll().contains("Output port is unavailable"), "Port failure closes device and reports reason");
        failure.add(keyboard(7, "Other", "", 0)); Handler.drain();
        check(failure.opens.size() == 1, "Open failure must not silently switch instruments");
        stop();
        QuestMidi.start(new Context(null)); Handler.drain();
        check(QuestMidi.poll().contains("service unavailable"), "Missing framework service is visible");
        stop();
        System.out.println("QuestMidiTest (simulated Android): PASS");
    }
}
