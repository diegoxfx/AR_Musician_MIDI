package com.armusicians.midi;

/** MIDI 1.0 byte-stream decoder. Android may split messages at any byte boundary. */
public final class MidiStreamDecoder {
    public interface Sink {
        void note(int channel, int note, int velocity, boolean on);
        void channelReset(int channel);
        void reset();
    }

    private final Sink sink;
    private int status;
    private int firstData;
    private int dataCount;
    private boolean inSysEx;

    public MidiStreamDecoder(Sink sink) {
        this.sink = sink;
    }

    public void clear() {
        status = 0;
        dataCount = 0;
        inSysEx = false;
    }

    public void send(byte[] bytes, int offset, int count) {
        if (bytes == null || offset < 0 || count < 0 || offset > bytes.length - count) {
            throw new IllegalArgumentException("Invalid MIDI byte range");
        }
        for (int i = offset; i < offset + count; i++) {
            int value = bytes[i] & 0xff;
            // Realtime messages can occur even between the bytes of a channel message.
            if (value >= 0xf8) {
                if (value == 0xff) {
                    clear();
                    sink.reset();
                }
                continue;
            }
            if ((value & 0x80) != 0) {
                dataCount = 0;
                inSysEx = value == 0xf0;
                // System Common and SysEx cancel channel running status.
                status = value < 0xf0 ? value : 0;
                continue;
            }
            if (inSysEx || status == 0) continue;
            int command = status & 0xf0;
            if (command == 0xc0 || command == 0xd0) continue; // One data byte, no note.
            if (dataCount == 0) {
                firstData = value;
                dataCount = 1;
                continue;
            }
            dataCount = 0;
            int channel = status & 0x0f;
            if (command == 0x80 || command == 0x90) {
                sink.note(channel, firstData, value, command == 0x90 && value != 0);
            } else if (command == 0xb0 && (firstData == 120 || firstData == 123)) {
                sink.channelReset(channel);
            }
        }
    }
}
