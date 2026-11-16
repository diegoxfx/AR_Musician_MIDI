package android.media.midi;
import java.io.Closeable;
public class MidiOutputPort implements Closeable {
    public MidiReceiver receiver;
    public boolean closed;
    public void connect(MidiReceiver receiver) { this.receiver = receiver; }
    public void disconnect(MidiReceiver receiver) { if (this.receiver == receiver) this.receiver = null; }
    @Override public void close() { closed = true; }
    public void send(int... bytes) {
        if (receiver == null) throw new AssertionError("Receiver not connected");
        byte[] data = new byte[bytes.length];
        for (int i = 0; i < bytes.length; i++) data[i] = (byte) bytes[i];
        receiver.onSend(data, 0, data.length, 0);
    }
}
