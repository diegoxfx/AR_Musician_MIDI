package com.armusicians.midi;

import java.util.ArrayList;
import java.util.Arrays;
import java.util.List;

/** Dependency-free regression test: run with tests/java/test.ps1 or javac/java. */
public final class MidiStreamDecoderTest {
    private static final class Recorder implements MidiStreamDecoder.Sink {
        final List<String> events = new ArrayList<String>();
        @Override public void note(int channel, int note, int velocity, boolean on) {
            events.add(channel + ":" + note + ":" + velocity + ":" + on);
        }
        @Override public void channelReset(int channel) { events.add("channelReset:" + channel); }
        @Override public void reset() { events.add("reset"); }
    }

    private static void send(MidiStreamDecoder decoder, int... data) {
        byte[] bytes = new byte[data.length];
        for (int i = 0; i < data.length; i++) bytes[i] = (byte) data[i];
        decoder.send(bytes, 0, bytes.length);
    }

    private static void expect(Recorder recorder, String... expected) {
        if (!recorder.events.equals(Arrays.asList(expected))) {
            throw new AssertionError("Expected " + Arrays.toString(expected) + " but got " + recorder.events);
        }
        recorder.events.clear();
    }

    public static void main(String[] args) {
        Recorder recorder = new Recorder();
        MidiStreamDecoder decoder = new MidiStreamDecoder(recorder);
        // Running status across packets, realtime inside a message, explicit note off, vel-zero off.
        send(decoder, 0x92, 60);
        send(decoder, 0xf8, 100, 61, 110, 62);
        send(decoder, 0xfa, 0, 0x82, 60, 34);
        expect(recorder, "2:60:100:true", "2:61:110:true", "2:62:0:false", "2:60:34:false");

        // Every possible packet boundary must produce the same complete messages.
        int[] stream = {0x90, 60, 100, 61, 50, 0xf8, 62, 0, 0xb3, 123, 0, 0x83, 60, 10};
        for (int split = 0; split <= stream.length; split++) {
            decoder.clear();
            send(decoder, Arrays.copyOfRange(stream, 0, split));
            send(decoder, Arrays.copyOfRange(stream, split, stream.length));
            expect(recorder, "0:60:100:true", "0:61:50:true", "0:62:0:false", "channelReset:3", "3:60:10:false");
        }

        // Channel modes only affect their own channel; unrelated CC is ignored.
        send(decoder, 0xb4, 120, 0, 123, 0, 64, 127);
        expect(recorder, "channelReset:4", "channelReset:4");
        // One-byte channel messages and their running status never become accidental notes.
        send(decoder, 0xc0, 4, 7, 0xd0, 8, 9, 0xa0, 60, 80, 0xe0, 0, 64);
        expect(recorder);

        // System common and SysEx cancel running status; realtime does not end SysEx.
        for (int common : new int[] {0xf0, 0xf1, 0xf2, 0xf3, 0xf4, 0xf5, 0xf6, 0xf7}) {
            send(decoder, 0x90, 60, 100, common, 61, 0xf8, 100, 0xf7, 62, 100, 0x91, 63, 100);
            expect(recorder, "0:60:100:true", "1:63:100:true");
        }
        // Explicit clear and system reset discard partial messages and running status.
        send(decoder, 0x90, 60);
        decoder.clear();
        send(decoder, 100, 61, 100, 0x90, 62, 0xff, 100, 63, 100);
        expect(recorder, "reset");

        // Respect offset/count, and reject invalid ranges before reading them.
        decoder.send(new byte[] {1, (byte) 0x9f, 127, 127, 1}, 1, 3);
        expect(recorder, "15:127:127:true");
        for (int[] range : new int[][] {{-1, 1}, {0, -1}, {2, 1}, {1, Integer.MAX_VALUE}}) {
            try {
                decoder.send(new byte[2], range[0], range[1]);
                throw new AssertionError("Invalid range accepted");
            } catch (IllegalArgumentException expected) { }
        }
        System.out.println("MidiStreamDecoderTest: PASS");
    }
}
