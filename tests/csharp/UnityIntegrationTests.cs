// Compiles real production scripts against lightweight Unity/project stubs.
// This is a desktop integration regression suite, NOT a Unity player/build test.
using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using ARMusician.Midi;
using UnityEngine;

namespace UnityEngine
{
    public class Object
    {
        internal static readonly List<Component> Components = new List<Component>();
        public static T FindFirstObjectByType<T>() where T : Component { return Components.OfType<T>().FirstOrDefault(); }
        public static void Destroy(Object value)
        { var c = value as Component; if (c != null) Components.Remove(c); var go = value as GameObject; if (go != null) Components.RemoveAll(item=>item.gameObject==go); }
        public static GameObject Instantiate(GameObject source)
        {
            var copy = new GameObject(source.name + " clone");
            var renderer = source.GetComponent<Renderer>();
            if (renderer != null) copy.AddComponent<MeshRenderer>().sharedMaterial = renderer.sharedMaterial;
            return copy;
        }
    }
    public class Component : Object
    {
        public GameObject gameObject;
        public Transform transform { get { return gameObject.transform; } }
        public T GetComponent<T>() where T : Component { return gameObject.GetComponent<T>(); }
    }
    public class MonoBehaviour : Component { public object StartCoroutine(IEnumerator routine) { return routine; } }
    public class GameObject : Object
    {
        public string name;
        public bool activeSelf = true;
        public Transform transform;
        private readonly List<Component> components = new List<Component>();
        public GameObject(string name = "test") { this.name = name; transform = new Transform { gameObject = this }; }
        public T AddComponent<T>() where T : Component, new()
        {
            var component = new T { gameObject = this }; components.Add(component); Components.Add(component);
            if (component is TextMesh && GetComponent<MeshRenderer>() == null) AddComponent<MeshRenderer>();
            return component;
        }
        public T GetComponent<T>() where T : Component { return components.OfType<T>().FirstOrDefault(); }
        public void SetActive(bool active) { activeSelf = active; }
        public static GameObject CreatePrimitive(PrimitiveType type)
        { var value = new GameObject(); value.AddComponent<MeshRenderer>(); value.AddComponent<Collider>(); return value; }
    }
    public class Transform : Component
    {
        public Vector3 localScale = new Vector3(1, 1, 1), localPosition, position;
        public Quaternion rotation;
        public Transform parent;
        public Vector3 up { get { return Vector3.up; } }
        public void SetParent(Transform value, bool worldPositionStays) { parent = value; }
        public Vector3 TransformPoint(Vector3 point) { return position + new Vector3(point.x * localScale.x, point.y * localScale.y, point.z * localScale.z); }
        public void SetPositionAndRotation(Vector3 value, Quaternion orientation) { position = value; rotation = orientation; }
    }
    public struct Vector3
    {
        public float x, y, z;
        public Vector3(float x, float y, float z) { this.x = x; this.y = y; this.z = z; }
        public static Vector3 zero { get { return new Vector3(); } }
        public static Vector3 up { get { return new Vector3(0, 1, 0); } }
        public float magnitude { get { return (float)Math.Sqrt(x*x + y*y + z*z); } }
        public Vector3 normalized { get { return magnitude == 0 ? zero : this / magnitude; } }
        public static Vector3 operator +(Vector3 a, Vector3 b) { return new Vector3(a.x+b.x,a.y+b.y,a.z+b.z); }
        public static Vector3 operator -(Vector3 a, Vector3 b) { return new Vector3(a.x-b.x,a.y-b.y,a.z-b.z); }
        public static Vector3 operator *(Vector3 a, float b) { return new Vector3(a.x*b,a.y*b,a.z*b); }
        public static Vector3 operator /(Vector3 a, float b) { return new Vector3(a.x/b,a.y/b,a.z/b); }
        public static float Distance(Vector3 a, Vector3 b) { return (a-b).magnitude; }
        public static Vector3 Lerp(Vector3 a, Vector3 b, float t) { return a+(b-a)*t; }
        public static Vector3 Cross(Vector3 a, Vector3 b) { return new Vector3(a.y*b.z-a.z*b.y,a.z*b.x-a.x*b.z,a.x*b.y-a.y*b.x); }
    }
    public struct Quaternion
    { public static Quaternion LookRotation(Vector3 forward) { return new Quaternion(); } public static Quaternion LookRotation(Vector3 forward, Vector3 up) { return new Quaternion(); } }
    public struct Color
    {
        private int value;
        public static Color white=new Color {value=1}, black=new Color {value=2}, green=new Color {value=3}, red=new Color {value=4}, cyan=new Color {value=5}, darkCyan=new Color {value=6};
        public static bool operator ==(Color a, Color b) { return a.value==b.value; }
        public static bool operator !=(Color a, Color b) { return a.value!=b.value; }
        public override bool Equals(object other) { return other is Color && this==(Color)other; }
        public override int GetHashCode() { return value; }
    }
    public class Material : Object { public Color color; public Material() {} public Material(Material source) {} }
    public class MaterialPropertyBlock { public void SetColor(string name, Color color) {} }
    public class Renderer : Component
    { public Material sharedMaterial = new Material(); public Material material = new Material(); public void SetPropertyBlock(MaterialPropertyBlock block) {} }
    public class MeshRenderer : Renderer {}
    public class Collider : Component {}
    public class Font : Object { public Material material = new Material(); }
    public class TextMesh : Component
    { public Font font; public int fontSize; public float characterSize; public TextAnchor anchor; public TextAlignment alignment; public Color color; public string text; }
    public class Camera : Component { public static Camera main; }
    public class AudioSource : Component {}
    public class AndroidJavaClass : IDisposable
    { public AndroidJavaClass(string name) {} public void CallStatic(string name, params object[] args) {} public T CallStatic<T>(string name, params object[] args) { return default(T); } public T GetStatic<T>(string name) { return default(T); } public void Dispose() {} }
    public class AndroidJavaObject : IDisposable { public void Dispose() {} }
    public class WaitForSeconds { public WaitForSeconds(float value) {} }
    public static class JsonUtility { public static T FromJson<T>(string json) { throw new NotSupportedException("JSON parsing is outside this stub integration suite."); } }
    public static class Resources { public static T GetBuiltinResource<T>(string name) where T : new() { return new T(); } }
    public static class Mathf { public static float Abs(float value) { return Math.Abs(value); } public static float Max(float a, float b) { return Math.Max(a, b); } public static float Clamp01(float value) { return Math.Max(0,Math.Min(1,value)); } }
    public static class Time { public static float time, unscaledTime; }
    public static class Application { public static string streamingAssetsPath; }
    public static class Debug { public static void Log(object value) {} public static void LogWarning(object value) {} public static void LogError(object value) {} }
    public class HeaderAttribute : Attribute { public HeaderAttribute(string label) {} }
    public class TooltipAttribute : Attribute { public TooltipAttribute(string label) {} }
    public class ExecuteAlways : Attribute {}
    public enum TextAnchor { MiddleCenter }
    public enum TextAlignment { Center }
    public enum PrimitiveType { Cube }
}
namespace UnityEngine.Networking { public class UnusedNetworkingStub {} }
namespace Newtonsoft.Json
{ public static class JsonConvert { public static T DeserializeObject<T>(string json) { throw new NotSupportedException("JSON loading is outside this stub integration suite."); } } }
public static class OVRInput
{
    public enum Button { One, Two }
    public enum Controller { RTouch, LTouch }
    private static Button? nextButton;
    private static Controller nextController;
    public static void Press(Button button, Controller controller) { nextButton = button; nextController = controller; }
    public static bool GetDown(Button button, Controller controller)
    { if (nextButton == button && nextController == controller) { nextButton = null; return true; } return false; }
}
public class DefinedPlane { public Plane Plane; public Vector3 Center, Corner1, Corner2, Corner3, Corner4; }
public class Plane { public Vector3 normal; }
public static class PianoManagerInstrumentDefiner { public static event Action<DefinedPlane> OnPlaneDefined; public static void Define(DefinedPlane plane) { OnPlaneDefined?.Invoke(plane); } }
public static class ProjectConfig { public static readonly SettingsData Settings = new SettingsData(); public class SettingsData { public bool playing, master, useMultiplayer, enableMultiplayer; } }
public class MenuControllerGeneric
{
    public int SongMenuCalls, PauseMenuCalls;
    public void ShowSongMenu() { SongMenuCalls++; }
    public void ShowSingleplayerPauseMenu() { PauseMenuCalls++; }
    public void ShowMultiplayerPauseMenu(bool masterPauser) { PauseMenuCalls++; }
}
public class PartialSongData { public string JsonPath; }
public class SongData { public PartialSongData PartialSongData; }
public class BongoStrategy : MonoBehaviour, IGameplayStrategy
{
    public int average_key;
    public float ApproachTime { get { return 2; } }
    public bool IsInstrumentReady() { return true; }
    public void PreprocessNote(NoteEvent note) {}
    public void SpawnNote(NoteEvent note, float speed) { OnNoteFinished?.Invoke(); }
    public event Action OnNoteFinished;
}

public static class UnityIntegrationTests
{
    private static int assertions;
    private static void Check(bool condition, string message) { assertions++; if (!condition) throw new Exception(message); }
    private static void Near(double expected, double actual, string message) { Check(Math.Abs(expected-actual)<0.0001, message + " actual=" + actual); }
    private static object Field(object value, string name) { return value.GetType().GetField(name, BindingFlags.Instance|BindingFlags.NonPublic).GetValue(value); }
    private static void Set(object value, string name, object content) { value.GetType().GetField(name, BindingFlags.Instance|BindingFlags.NonPublic).SetValue(value, content); }
    private static void Call(object value, string name, params object[] args) { value.GetType().GetMethod(name, BindingFlags.Instance|BindingFlags.NonPublic).Invoke(value,args); }
    private static void Send(PianoMidiFeedback feedback, string type, int note = 60, int channel = 0, bool on = true, int velocity = 100)
    {
        var eventType = typeof(PianoMidiFeedback).GetNestedType("MidiEvent", BindingFlags.NonPublic);
        var message = Activator.CreateInstance(eventType);
        foreach (var entry in new Dictionary<string,object> { {"type",type},{"note",note},{"channel",channel},{"on",on},{"velocity",velocity} })
            eventType.GetField(entry.Key).SetValue(message,entry.Value);
        Call(feedback,"Receive",message);
    }
    private sealed class Fixture
    {
        public RhythmGameManager Game;
        public PianoStrategy Piano;
        public PianoMidiFeedback Feedback { get { return Piano.MidiFeedback; } }
        public PlaneController Plane;
        public Fixture()
        {
            UnityEngine.Object.Components.Clear();
            Time.time = Time.unscaledTime = 0;
            Game = new GameObject().AddComponent<RhythmGameManager>(); Game.menucontroller = new MenuControllerGeneric();
            Plane = new GameObject().AddComponent<PlaneController>(); Plane.leftmostKey = "C2"; Plane.totalKeys = 61; Plane.reInit();
            Piano = new GameObject().AddComponent<PianoStrategy>(); Piano.plane = Plane;
            Piano.noteCubePrefab = GameObject.CreatePrimitive(PrimitiveType.Cube);
            Game.SetStrategy(Piano);
            Call(Piano,"Start");
            Call(Piano,"UpdatePlaneVisuals",new DefinedPlane { Plane = new Plane { normal = new Vector3(0,1,0) }, Corner1 = new Vector3(-1,0,0),Corner2 = new Vector3(1,0,0),Corner3 = new Vector3(1,0,1),Corner4 = new Vector3(-1,0,1) });
        }
        public void Calibrate()
        { Call(Feedback,"BeginCalibration"); Send(Feedback,"note",36); Send(Feedback,"note",96); }
        public void Load(params NoteEvent[] notes) { Set(Game,"loadedNotes",new List<NoteEvent>(notes)); }
        public PracticeMatcher Matcher { get { return (PracticeMatcher)Field(Feedback,"matcher"); } }
    }

    private static void CalibrationAndHotplug()
    {
        var f = new Fixture();
        Check(f.Piano.IsInstrumentReady(), "Vision plane establishes instrument readiness");
        Call(f.Feedback,"BeginCalibration");
        Send(f.Feedback,"note",37);
        Check(!((bool)Field(f.Feedback,"rangeReady")),"Black initial key cannot calibrate");
        Send(f.Feedback,"note",36); Send(f.Feedback,"note",84);
        Check(!((bool)Field(f.Feedback,"rangeReady")),"Wrong octave must not silently calibrate");
        Send(f.Feedback,"note",96);
        Check((bool)Field(f.Feedback,"rangeReady"),"Retry correct high endpoint completes calibration");
        Check(f.Plane.totalKeys==61 && f.Plane.leftmostKey=="C2","Explicit MIDI calibration controls plane range");
        Check(f.Plane.GetLocalKeyWidth(96)>0 && f.Plane.GetLocalKeyWidth(97)==0,"Highest C represented and out-of-range key excluded");
        Check(((Dictionary<int,GameObject>)Field(f.Feedback,"markers")).Count==61,"61 physical markers generated");
        float left=f.Plane.GetLocalKeyPosition(36).x;
        f.Plane.reverseHorizontal=true;
        Near(-left,f.Plane.GetLocalKeyPosition(36).x,"User reversal mirrors physical mapping");
        Send(f.Feedback,"note",60,0); Send(f.Feedback,"note",60,1);
        Send(f.Feedback,"channelReset",60,0);
        Check(((HeldNotes)Field(f.Feedback,"held")).IsHeld(60),"Channel reset preserves another channel's pressed key");
        Send(f.Feedback,"reset");
        Check(!((HeldNotes)Field(f.Feedback,"held")).IsHeld(60),"Hotplug/reset clears held keys");
        Check(!(bool)Field(f.Feedback,"rangeReady") && !(bool)Field(f.Feedback,"scoring") && !(bool)Field(f.Feedback,"calibrating"),"Hotplug invalidates physical range/scoring/calibration");
        Call(f.Feedback,"Update");
        Check(((Dictionary<int,GameObject>)Field(f.Feedback,"markers")).Values.All(marker=>!marker.activeSelf),"Reset hides all stale markers next update");
        f.Calibrate();
        Check((bool)Field(f.Feedback,"rangeReady"),"Device can calibrate after reset");
        f.Feedback.BeginSong(new List<NoteEvent> {
            new NoteEvent {keyIndex=35,time=0}, new NoteEvent {keyIndex=36,time=0},
            new NoteEvent {keyIndex=96,time=0}, new NoteEvent {keyIndex=97,time=0} },1);
        Check(f.Matcher.Total==2 && (int)Field(f.Feedback,"skipped")==2,"Song matching includes both range endpoints and counts excluded notes");
        f.Feedback.EndSong(false);
        Check(f.Matcher.Misses==0,"User quit does not finalize unfinished scheduled notes as misses");
        OVRInput.Press(OVRInput.Button.Two,OVRInput.Controller.LTouch); Call(f.Feedback,"Update");
        f.Feedback.BeginSong(new List<NoteEvent> {new NoteEvent {keyIndex=60,time=0}},1);
        Check(!(bool)Field(f.Feedback,"scoring") && f.Matcher.Total==0,"Feedback-off mode does not schedule scoring");
    }

    private static void PauseReplayAndTiming()
    {
        var f = new Fixture(); f.Calibrate();
        var original = new NoteEvent { key="C5",midiNumber=60,time=2,duration=4 };
        f.Load(original); f.Game.gameSpeed=2; Time.time=100;
        f.Game.Play();
        Check(f.Game.playing && f.Game.HasSession && f.Matcher.Total==1,"Play creates scored session");
        Near(-2,f.Game.SongTime,"Approach lead-in starts clock before first strike");
        Time.time=101; f.Game.Play();
        Near(-1,f.Game.SongTime,"Duplicate Play does not restart an active song clock");
        Time.time=102.5f; f.Game.Pause();
        Check(!f.Game.playing && f.Game.HasSession,"Pause preserves session");
        Send(f.Feedback,"note",60); Call(f.Feedback,"Update");
        Check(f.Matcher.Hits==0 && f.Matcher.Wrong==0 && f.Matcher.Misses==0,"Paused MIDI cannot score or expire notes");
        Time.time=104; f.Game.Pause(); f.Game.Play();
        Check(!f.Game.playing,"Play while paused cannot replace the existing session");
        Time.time=110.5f; f.Game.Resume(); Time.time=110.75f; f.Game.Resume(); Time.time=111;
        Near(1,f.Game.SongTime,"Resume removes exactly paused wall-clock time");
        Send(f.Feedback,"note",60);
        Check(f.Matcher.Hits==1,"Song note timing divides source time by speed and excludes pause");
        Call(f.Game,"Update");
        var firstCube=UnityEngine.Object.Components.OfType<CubeFallBehavior>().Single();
        Near(2,firstCube.duration,"Spawn duration divided by speed once");
        Call(firstCube,"Start");
        // A late frame spawned the visual exactly at its strike time. It must already
        // be at the bottom, using the same clock as the successful MIDI strike.
        Call(firstCube,"Update");
        Near(f.Plane.transform.position.y-0.5+firstCube.origBlockHeight/2/f.Plane.height,firstCube.transform.position.y,"Late-spawned cube is already at its strike line");
        Near(4,original.duration,"Play and Update preserve loaded song duration");
        f.Game.Quit();
        Check(!f.Game.HasSession && !f.Game.playing,"Quit ends active session");
        Time.time=120; f.Game.Play(); Time.time=121; Call(f.Game,"Update");
        var replayCube=UnityEngine.Object.Components.OfType<CubeFallBehavior>().Single();
        Near(2,replayCube.duration,"Replay uses fresh note clone without compounded speed scaling");
        Near(4,original.duration,"Replay retains original loaded note");
        Check(f.Matcher.Total==1 && f.Matcher.Hits==0,"Replay resets scoring schedule and counters");
        Time.time=123; Send(f.Feedback,"note",60);
        Check(f.Matcher.Hits==1,"Replay timing uses a new session clock");
        f.Game.Quit();
    }

    private static void ZeroDurationCompletion()
    {
        var f=new Fixture(); f.Calibrate();
        f.Load(new NoteEvent {midiNumber=60,time=0,duration=0});
        Time.time=10; f.Game.Play(); Call(f.Game,"Update");
        var cube=UnityEngine.Object.Components.OfType<CubeFallBehavior>().Single();
        Call(cube,"Start"); Time.time=12; Call(cube,"Update");
        Check((bool)Field(cube,"done"),"Zero-duration visual completes without division by zero");
        Check((int)Field(f.Game,"activeNoteCount")==0,"Completed zero-duration visual decrements active note count");
        Near(0,cube.transform.localScale.y,"Completed zero-duration visual collapses to zero height");
        Time.time=12.31f; Call(f.Game,"Update");
        Check(!f.Game.HasSession && f.Matcher.Misses==1,"Song completes and finalizes unmatched zero-duration event");
    }

    private static void LifecycleAndCanonicalPitch()
    {
        var f=new Fixture();
        f.Game.Resume();
        Check(!f.Game.playing && !f.Game.HasSession,"Resume while idle cannot create a phantom session");
        f.Game.Pause();
        Check(!f.Game.playing && !f.Game.HasSession,"Pause while idle remains idle");
        var legacy=new NoteEvent { key="D5" }; f.Piano.PreprocessNote(legacy);
        Check(legacy.keyIndex==62,"Legacy bundled pitch retains original MIDI number");
        var canonical=new NoteEvent { key="C9",midiNumber=60 }; f.Piano.PreprocessNote(canonical);
        Check(canonical.keyIndex==60,"Canonical numeric pitch overrides display name/legacy offset");
        f.Calibrate(); f.Load(new NoteEvent { midiNumber=60,time=2,duration=1 });
        Time.time=10; f.Game.Play(); Time.time=11;
        Call(f.Feedback,"OnApplicationPause",true);
        Check(f.Game.HasSession && !f.Game.playing,"App suspension pauses active song");
        Check(f.Game.menucontroller.PauseMenuCalls>0,"Suspension exposes resume/quit menu");
        Check(!(bool)Field(f.Feedback,"rangeReady") && !(bool)Field(f.Feedback,"scoring"),"Suspension resets MIDI alignment and scoring");
        Call(f.Feedback,"OnApplicationPause",false);
        Check(!f.Game.playing,"App focus return does not unexpectedly resume song");
        f.Game.Quit();
    }

    public static string Run()
    {
        assertions=0;
        CalibrationAndHotplug(); PauseReplayAndTiming(); ZeroDurationCompletion(); LifecycleAndCanonicalPitch();
        return "PASS: " + assertions + " Unity integration assertions with stubs (not a real Unity build)";
    }
}
