package android.media.midi;
public abstract class MidiReceiver {
    public abstract void onSend(byte[] data, int offset, int count, long timestamp);
    public void onFlush() { }
}
