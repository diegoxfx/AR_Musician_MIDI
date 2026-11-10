package android.content;
import android.media.midi.MidiManager;
public class Context {
    public static final String MIDI_SERVICE = "midi";
    private final MidiManager manager;
    public Context(MidiManager manager) { this.manager = manager; }
    public Context getApplicationContext() { return this; }
    public Object getSystemService(String service) { return manager; }
}
