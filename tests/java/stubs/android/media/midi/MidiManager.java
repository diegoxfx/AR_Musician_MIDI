package android.media.midi;
import android.os.Handler;
import java.util.ArrayList;
public class MidiManager {
    public interface OnDeviceOpenedListener { void onDeviceOpened(MidiDevice device); }
    public static class DeviceCallback {
        public void onDeviceAdded(MidiDeviceInfo info) { }
        public void onDeviceRemoved(MidiDeviceInfo info) { }
    }
    public static class OpenRequest {
        public final MidiDeviceInfo info;
        final OnDeviceOpenedListener listener;
        final Handler handler;
        OpenRequest(MidiDeviceInfo info, OnDeviceOpenedListener listener, Handler handler) {
            this.info = info; this.listener = listener; this.handler = handler;
        }
    }
    public final ArrayList<MidiDeviceInfo> devices = new ArrayList<MidiDeviceInfo>();
    public final ArrayList<OpenRequest> opens = new ArrayList<OpenRequest>();
    public DeviceCallback callback;
    private Handler handler;
    public int registrations;
    public MidiDeviceInfo[] getDevices() { return devices.toArray(new MidiDeviceInfo[devices.size()]); }
    public void registerDeviceCallback(DeviceCallback callback, Handler handler) {
        this.callback = callback; this.handler = handler; registrations++;
    }
    public void unregisterDeviceCallback(DeviceCallback callback) { if (this.callback == callback) this.callback = null; }
    public void openDevice(MidiDeviceInfo info, OnDeviceOpenedListener listener, Handler handler) {
        opens.add(new OpenRequest(info, listener, handler));
    }
    public void completeOpen(int index, final MidiDevice device) {
        final OpenRequest request = opens.get(index);
        request.handler.post(new Runnable() { @Override public void run() { request.listener.onDeviceOpened(device); } });
    }
    public void add(final MidiDeviceInfo info) {
        devices.add(info);
        final DeviceCallback target = callback;
        if (target != null) handler.post(new Runnable() { @Override public void run() { target.onDeviceAdded(info); } });
    }
    public void remove(final MidiDeviceInfo info) {
        devices.remove(info);
        final DeviceCallback target = callback;
        if (target != null) handler.post(new Runnable() { @Override public void run() { target.onDeviceRemoved(info); } });
    }
}
