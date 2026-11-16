package android.os;
import java.util.HashMap;
public class Bundle {
    private final HashMap<String, String> strings = new HashMap<String, String>();
    public String getString(String key) { return strings.get(key); }
    public String getString(String key, String fallback) { return strings.containsKey(key) ? strings.get(key) : fallback; }
    public void putString(String key, String value) { strings.put(key, value); }
}
