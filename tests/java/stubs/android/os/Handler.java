package android.os;
import java.util.ArrayDeque;
/** Deterministic test main looper; this is not shipped in the Unity project. */
public class Handler {
    private static final ArrayDeque<Runnable> queue = new ArrayDeque<Runnable>();
    public Handler(Looper looper) { }
    public boolean post(Runnable runnable) { queue.addLast(runnable); return true; }
    public static void drain() { while (!queue.isEmpty()) queue.removeFirst().run(); }
}
