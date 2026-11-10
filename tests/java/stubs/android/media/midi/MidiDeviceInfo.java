package android.media.midi;
import android.os.Bundle;
public class MidiDeviceInfo {
    public static final int TYPE_USB = 1;
    public static final String PROPERTY_NAME = "name", PROPERTY_MANUFACTURER = "manufacturer", PROPERTY_PRODUCT = "product";
    private final int id, type;
    private final PortInfo[] ports;
    private final Bundle properties = new Bundle();
    public MidiDeviceInfo(int id, int type, String name, String manufacturer, int... outputPorts) {
        this.id = id; this.type = type;
        properties.putString(PROPERTY_NAME, name);
        properties.putString(PROPERTY_MANUFACTURER, manufacturer);
        ports = new PortInfo[outputPorts.length];
        for (int i = 0; i < ports.length; i++) ports[i] = new PortInfo(outputPorts[i]);
    }
    public int getId() { return id; }
    public int getType() { return type; }
    public int getOutputPortCount() { return ports.length; }
    public Bundle getProperties() { return properties; }
    public PortInfo[] getPorts() { return ports; }
    public static class PortInfo {
        public static final int TYPE_OUTPUT = 2;
        private final int number;
        public PortInfo(int number) { this.number = number; }
        public int getType() { return TYPE_OUTPUT; }
        public int getPortNumber() { return number; }
    }
}
