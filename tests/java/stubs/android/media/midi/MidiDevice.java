package android.media.midi;
import java.io.Closeable;
public class MidiDevice implements Closeable {
    public final MidiOutputPort output = new MidiOutputPort();
    public boolean closed, failPort;
    public int openedPort = -1;
    public MidiOutputPort openOutputPort(int number) { openedPort = number; return failPort ? null : output; }
    @Override public void close() { closed = true; }
}
