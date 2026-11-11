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
    public class Coroutine { public IEnumerator Routine; public bool Cancelled; }
    public class MonoBehaviour : Component
    {
        public readonly List<Coroutine> StartedCoroutines = new List<Coroutine>();
        public Coroutine StartCoroutine(IEnumerator routine)
        {
            var handle = new Coroutine { Routine = routine }; StartedCoroutines.Add(handle);
            // Unity runs the first part immediately, up to its first yield.
            routine.MoveNext(); return handle;
        }
        public void StopCoroutine(Coroutine routine) { routine.Cancelled = true; }
        public void StopAllCoroutines() { foreach (var item in StartedCoroutines) item.Cancelled = true; }
    }
    public class GameObject : Object
    {
        public string name;
        public static bool SuppressTextMeshRenderer;
        public bool activeSelf = true;
        public Transform transform;
        private readonly List<Component> components = new List<Component>();
        public GameObject(string name = "test") { this.name = name; transform = new Transform { gameObject = this }; }
        public T AddComponent<T>() where T : Component, new()
        {
            var component = new T { gameObject = this }; components.Add(component); Components.Add(component);
            if (component is TextMesh && !SuppressTextMeshRenderer && GetComponent<MeshRenderer>() == null) AddComponent<MeshRenderer>();
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
    public class Shader : Object {}
    public class Material : Object
    {
        public Color color; public Shader shader = new Shader();
        public Material() {}
        public Material(Material source)
        { if (source == null) throw new ArgumentNullException("source"); shader = source.shader; color = source.color; }
    }
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
    public static class Resources
    {
        public static bool MissingFont, MissingFontMaterial;
        public static T GetBuiltinResource<T>(string name) where T : new()
        {
            if (MissingFont) return default(T);
            var value = new T(); var font = value as Font;
            if (font != null && MissingFontMaterial) font.material = null;
            return value;
        }
    }
    public static class Mathf { public static float Abs(float value) { return Math.Abs(value); } public static float Max(float a, float b) { return Math.Max(a, b); } public static float Clamp01(float value) { return Math.Max(0,Math.Min(1,value)); } public static float Ceil(float value) { return (float)Math.Ceiling(value); } }
    public static class Time { public static float time, unscaledTime, deltaTime = 1, unscaledDeltaTime = 1; }
    public static class Application { public static string streamingAssetsPath; }
    public static class Debug { public static readonly List<string> Warnings = new List<string>(); public static void Log(object value) {} public static void LogWarning(object value) { Warnings.Add(value.ToString()); } public static void LogError(object value) {} }
    public class HeaderAttribute : Attribute { public HeaderAttribute(string label) {} }
    public class TooltipAttribute : Attribute { public TooltipAttribute(string label) {} }
    public class ExecuteAlways : Attribute {}
    public class SerializeField : Attribute {}
    public class SpaceAttribute : Attribute { public SpaceAttribute(float value) {} }
    public enum TextAnchor { MiddleCenter }
    public enum TextAlignment { Center }
    public enum PrimitiveType { Cube }
}
namespace UnityEngine.UI { public class Button : Component { public bool interactable; } public class Image : Component { public float fillAmount; } }
namespace TMPro { public class TextMeshProUGUI : Component { public string text; } }
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
    public static void Reset() { nextButton=null; }
    public static bool GetDown(Button button, Controller controller)
    { if (nextButton == button && nextController == controller) { nextButton = null; return true; } return false; }
}
public static class OVRManager
{
    public static event Action InputFocusLost, InputFocusAcquired;
    public static void LoseFocus() { InputFocusLost?.Invoke(); }
    public static void AcquireFocus() { InputFocusAcquired?.Invoke(); }
    public static void Reset() { InputFocusLost=null; InputFocusAcquired=null; }
}
public class DefinedPlane { public Plane Plane; public Vector3 Center, Corner1, Corner2, Corner3, Corner4; }
public class Plane { public Vector3 normal; }
public interface IPointInputListener {}
public class RayCastInputProvider : MonoBehaviour { public void SetListener(IPointInputListener listener) {} }
public class PianoManagerInstrumentDefiner : MonoBehaviour, IPointInputListener { public static event Action<DefinedPlane> OnPlaneDefined; public bool IsDefining { get; private set; } public static void Define(DefinedPlane plane) { OnPlaneDefined?.Invoke(plane); } public void Activate(bool cv) { IsDefining=true; } public void Deactivate() { IsDefining=false; } }
public class BongosManager : MonoBehaviour, IPointInputListener { public static event Action OnBothBongosDefined; public void Activate(bool cv) {} public void Deactivate() {} }
public static class ProjectConfig { public static readonly SettingsData Settings = new SettingsData(); public class SettingsData { public bool playing, master, useMultiplayer, enableMultiplayer; public string instrument; public List<string> configured_instruments = new List<string>(); } }
public class PartialSongData { public string JsonPath, InlineJson; public bool IsExternal { get { return InlineJson != null; } } }
public class SongData { public PartialSongData PartialSongData; }
public class BongoStrategy : MonoBehaviour, IGameplayStrategy
{
    public int average_key;
    public float ApproachTime { get { return 2; } }
    public bool IsInstrumentReady() { return true; }
    public void PreprocessNote(NoteEvent note) { note.keyIndex = note.midiNumber; }
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
        public CountdownUIGeneric Countdown;
        public InstrumentSelectorController Selector;
        public BongoStrategy Bongo;
        public PianoManagerInstrumentDefiner Definer;
        public MenuControllerGeneric Menu { get { return Game.menucontroller; } }
        public Fixture(string missingResource = null, bool withDefiner = false)
        {
            // Each fixture represents a fresh scene; clear old scene event subscribers.
            foreach (string name in new [] {"OnPause","OnResume","OnQuit"})
                typeof(RhythmGameManager).GetField(name,BindingFlags.Static|BindingFlags.NonPublic).SetValue(null,null);
            typeof(PianoManagerInstrumentDefiner).GetField("OnPlaneDefined",BindingFlags.Static|BindingFlags.NonPublic).SetValue(null,null);
            OVRManager.Reset();
            OVRInput.Reset();
            Debug.Warnings.Clear();
            Resources.MissingFont = missingResource == "font";
            Resources.MissingFontMaterial = missingResource == "font material";
            GameObject.SuppressTextMeshRenderer = missingResource == "text renderer";
            UnityEngine.Object.Components.Clear();
            if (withDefiner) Definer = new GameObject().AddComponent<PianoManagerInstrumentDefiner>();
            Time.time = Time.unscaledTime = 0; Time.deltaTime = Time.unscaledDeltaTime = 1;
            ProjectConfig.Settings.playing = ProjectConfig.Settings.enableMultiplayer = ProjectConfig.Settings.useMultiplayer = false;
            ProjectConfig.Settings.master = true; ProjectConfig.Settings.instrument = null;
            Game = new GameObject().AddComponent<RhythmGameManager>();
            Call(Game,"OnEnable");
            Game.menucontroller = new GameObject().AddComponent<MenuControllerGeneric>();
            // Serialized scene references are assigned explicitly; the real menu code owns visibility.
            foreach (var field in typeof(MenuControllerGeneric).GetFields(BindingFlags.Instance|BindingFlags.Public|BindingFlags.NonPublic))
            {
                if (field.FieldType == typeof(GameObject)) field.SetValue(Menu,new GameObject(field.Name));
                else if (field.FieldType == typeof(UnityEngine.UI.Button)) field.SetValue(Menu,new GameObject(field.Name).AddComponent<UnityEngine.UI.Button>());
                else if (field.FieldType == typeof(UnityEngine.UI.Button[])) field.SetValue(Menu,new UnityEngine.UI.Button[0]);
            }
            Set(Menu,"rhythmGameManager",Game);
            Menu.HideAllMenus();
            Plane = new GameObject().AddComponent<PlaneController>(); Plane.leftmostKey = "C2"; Plane.totalKeys = 61; Plane.reInit();
            Piano = new GameObject().AddComponent<PianoStrategy>(); Piano.plane = Plane;
            Piano.noteCubePrefab = GameObject.CreatePrimitive(PrimitiveType.Cube);
            if (missingResource == "prefab") Piano.noteCubePrefab = null;
            if (missingResource == "renderer") Piano.noteCubePrefab = new GameObject("invalid prefab");
            if (missingResource == "material") Piano.noteCubePrefab.GetComponent<Renderer>().sharedMaterial = null;
            if (missingResource == "shader") Piano.noteCubePrefab.GetComponent<Renderer>().sharedMaterial.shader = null;
            Game.SetStrategy(Piano);
            Call(Piano,"Start");
            Resources.MissingFont = Resources.MissingFontMaterial = GameObject.SuppressTextMeshRenderer = false;
            Call(Piano,"UpdatePlaneVisuals",new DefinedPlane { Plane = new Plane { normal = new Vector3(0,1,0) }, Corner1 = new Vector3(-1,0,0),Corner2 = new Vector3(1,0,0),Corner3 = new Vector3(1,0,1),Corner4 = new Vector3(-1,0,1) });
            Bongo = new GameObject().AddComponent<BongoStrategy>();
            Selector = new GameObject().AddComponent<InstrumentSelectorController>();
            Set(Selector,"rhythmGameManager",Game); Set(Selector,"pianoStrategy",Piano); Set(Selector,"bongoStrategy",Bongo);
            Countdown = new GameObject().AddComponent<CountdownUIGeneric>();
            Countdown.rhythmGameManager=Game; Countdown.countdown=new GameObject("countdown"); Countdown.countdown.SetActive(false);
            Countdown.countdownText=new GameObject().AddComponent<TMPro.TextMeshProUGUI>();
            Countdown.countdownCircle=new GameObject().AddComponent<UnityEngine.UI.Image>();
        }
        public void Calibrate()
        { Call(Feedback,"BeginCalibration"); Send(Feedback,"note",36); Send(Feedback,"note",96); }
        public void Load(params NoteEvent[] notes) { Set(Game,"loadedNotes",new List<NoteEvent>(notes)); }
        public PracticeMatcher Matcher { get { return (PracticeMatcher)Field(Feedback,"matcher"); } }
    }

    private static void OptionalMidiInitializationFailure()
    {
        foreach (var missing in new [] { "prefab", "renderer", "material", "shader", "font", "font material", "text renderer" })
        {
            var f = new Fixture(missing);
            Check(!f.Feedback.IsInitialized, "Missing " + missing + " must fail MIDI initialization explicitly");
            Check(Debug.Warnings.Count == 1 && Debug.Warnings[0].StartsWith("USB MIDI disabled:") &&
                Debug.Warnings[0].IndexOf(missing == "text renderer" ? "MeshRenderer" : missing == "renderer" ? "Renderer" : missing.Split(' ')[0], StringComparison.OrdinalIgnoreCase) >= 0,
                "Missing " + missing + " produces one actionable resource diagnostic");
            Check(f.Piano.IsInstrumentReady(), "Optional MIDI failure preserves vision calibration for " + missing);
            Call(f.Feedback,"BeginCalibration");
            f.Feedback.BeginSong(new List<NoteEvent> { new NoteEvent { keyIndex=60,time=0 } },1);
            Send(f.Feedback,"note",36); Send(f.Feedback,"note",96); Send(f.Feedback,"note",60);
            Call(f.Feedback,"Update"); f.Feedback.EndSong(true);
            Check(!(bool)Field(f.Feedback,"calibrating") && !(bool)Field(f.Feedback,"scoring") &&
                !(bool)Field(f.Feedback,"rangeReady") && f.Matcher.Total==0 && f.Matcher.Hits==0 &&
                !((HeldNotes)Field(f.Feedback,"held")).IsHeld(60), "Failed MIDI cannot calibrate, hold keys, schedule or score for " + missing);
            Call(f.Feedback,"OnApplicationFocus",false); Call(f.Feedback,"OnApplicationPause",true);
            Check(f.Game.InputAllowed, "Failed optional MIDI lifecycle cannot block visual game input for " + missing);
            Check(Field(f.Feedback,"label")==null && Field(f.Feedback,"markerMaterial")==null &&
                !UnityEngine.Object.Components.OfType<TextMesh>().Any(), "Failed initialization releases visual resources for " + missing);

            // Repair resources and retry on the same component, rather than hiding a persistent partial state.
            f.Piano.noteCubePrefab=GameObject.CreatePrimitive(PrimitiveType.Cube);
            f.Feedback.Initialize(f.Piano);
            Check(f.Feedback.IsInitialized,"MIDI can initialize after repairing " + missing);
            f.Calibrate();
            Check((bool)Field(f.Feedback,"rangeReady"),"Repaired MIDI can calibrate after " + missing);
            Call(f.Feedback,"OnDestroy"); Call(f.Feedback,"OnDestroy");
            Call(f.Feedback,"Update");
            Check(!f.Feedback.IsInitialized && Field(f.Feedback,"label")==null &&
                ((Dictionary<int,GameObject>)Field(f.Feedback,"markers")).Count==0,
                "Destroy is idempotent and releases repaired MIDI for " + missing);
        }
    }

    private static void MidiYieldsToPianoDefinition()
    {
        var f = new Fixture(withDefiner:true); f.Calibrate();
        Send(f.Feedback,"note",60); Call(f.Feedback,"Update");
        var markers=(Dictionary<int,GameObject>)Field(f.Feedback,"markers");
        Check(markers[60].activeSelf && ((TextMesh)Field(f.Feedback,"label")).gameObject.activeSelf,"Calibrated MIDI shows held notes before reopening CV setup");
        bool reversed=f.Plane.reverseHorizontal;
        f.Definer.Activate(true);
        OVRInput.Press(OVRInput.Button.One,OVRInput.Controller.LTouch); Call(f.Feedback,"Update"); OVRInput.Reset();
        Check(f.Plane.reverseHorizontal==reversed && (bool)Field(f.Feedback,"rangeReady"),"Reopened CV setup owns X without changing the completed MIDI range or alignment");
        Check(!markers.Values.Any(m=>m.activeSelf) && !((TextMesh)Field(f.Feedback,"label")).gameObject.activeSelf &&
            !((HeldNotes)Field(f.Feedback,"held")).IsHeld(60),"Entering setup clears held notes and hides old-layout markers and label");
        OVRInput.Press(OVRInput.Button.Two,OVRInput.Controller.RTouch); Call(f.Feedback,"Update"); OVRInput.Reset();
        OVRInput.Press(OVRInput.Button.Two,OVRInput.Controller.LTouch); Call(f.Feedback,"Update"); OVRInput.Reset();
        Send(f.Feedback,"note",36); Send(f.Feedback,"note",96);
        Check(!(bool)Field(f.Feedback,"calibrating") && (bool)Field(f.Feedback,"rangeReady") && (bool)Field(f.Feedback,"enabledFeedback") &&
            !((HeldNotes)Field(f.Feedback,"held")).IsHeld(36) && !((HeldNotes)Field(f.Feedback,"held")).IsHeld(96),"Setup ignores B, Y and physical notes while preserving completed calibration");
        f.Definer.Deactivate();
        OVRInput.Press(OVRInput.Button.One,OVRInput.Controller.LTouch); Call(f.Feedback,"Update");
        Check(f.Plane.reverseHorizontal!=reversed && ((TextMesh)Field(f.Feedback,"label")).gameObject.activeSelf,"Fresh X works and MIDI label returns after setup finishes");

        f = new Fixture(withDefiner:true);
        Call(f.Feedback,"BeginCalibration"); Send(f.Feedback,"note",36);
        f.Definer.Activate(true); Send(f.Feedback,"note",96);
        Check(!(bool)Field(f.Feedback,"calibrating") && !(bool)Field(f.Feedback,"rangeReady") &&
            !((HeldNotes)Field(f.Feedback,"held")).IsHeld(36) && !((HeldNotes)Field(f.Feedback,"held")).IsHeld(96),"A setup-entry MIDI event cancels partial calibration before the next Update");
        f.Definer.Deactivate(); Call(f.Feedback,"Update"); Send(f.Feedback,"note",96);
        Check(!(bool)Field(f.Feedback,"rangeReady"),"Finishing setup cannot resume a stale partial calibration");
        f.Calibrate(); f.Definer.Activate(true); Send(f.Feedback,"reset");
        Check(!(bool)Field(f.Feedback,"rangeReady"),"USB reset still invalidates completed calibration during setup");

        f = new Fixture(withDefiner:true); f.Calibrate();
        f.Load(new NoteEvent {midiNumber=60,time=0,duration=1}); f.Game.Play();
        f.Definer.Activate(true); Time.time=100; Send(f.Feedback,"note",60); Call(f.Feedback,"Update"); f.Feedback.EndSong(true);
        Check(f.Matcher.Hits==0 && f.Matcher.Misses==0 && f.Matcher.Wrong==0 && !(bool)Field(f.Feedback,"scoring"),"Setup entry suppresses incoming scoring and per-frame/final matcher advancement");
        f.Game.Quit();
    }

    private static void CancelPianoDefinitionThroughMenu()
    {
        var f = new Fixture(withDefiner:true); f.Calibrate();
        Set(f.Menu,"pianoManager",f.Definer);
        f.Menu.ShowCVPlaneDefinitionMenu(); Call(f.Feedback,"Update");
        Check(f.Definer.IsDefining && ((GameObject)Field(f.Menu,"cvPlaneDefinitionMenu")).activeSelf,
            "Opening CV activates setup after navigation hides the previous menus");
        f.Menu.ShowMainMenu();
        Check(!f.Definer.IsDefining && f.Menu.mainMenu.activeSelf && !((GameObject)Field(f.Menu,"cvPlaneDefinitionMenu")).activeSelf,
            "Cancelling CV through Main Menu deactivates its hidden piano setup");
        bool reversed=f.Plane.reverseHorizontal;
        OVRInput.Press(OVRInput.Button.One,OVRInput.Controller.LTouch); Call(f.Feedback,"Update");
        Check((bool)Field(f.Feedback,"rangeReady") && f.Plane.reverseHorizontal!=reversed,
            "Cancelling CV retains completed MIDI calibration and immediately restores X control");
        f.Menu.StartPianoSetup();
        Check(f.Definer.IsDefining && ((GameObject)Field(f.Menu,"manualPlaneDefinitionMenu")).activeSelf,
            "Starting manual setup remains active after HideAllMenus cleanup");
        f.Menu.ShowCVPlaneDefinitionMenu();
        Check(f.Definer.IsDefining && ((GameObject)Field(f.Menu,"cvPlaneDefinitionMenu")).activeSelf &&
            !((GameObject)Field(f.Menu,"manualPlaneDefinitionMenu")).activeSelf,
            "Switching active manual setup to CV cleans up the old session and keeps the new setup active");
        f.Menu.ShowMainMenu();
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
        double pausedTime=f.Game.SongTime; Time.time=104;
        Near(pausedTime,f.Game.SongTime,"SongTime stays frozen in Paused while wall-clock time advances");
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
        Check(f.Menu.singleplayerPauseMenu.activeSelf,"Suspension exposes real resume/quit menu");
        Check(!(bool)Field(f.Feedback,"rangeReady") && !(bool)Field(f.Feedback,"scoring"),"Suspension resets MIDI alignment and scoring");
        Call(f.Feedback,"OnApplicationPause",false);
        Check(!f.Game.playing,"App focus return does not unexpectedly resume song");
        f.Game.Quit();
    }

    // Deliberately bypass StopCoroutine: a queued/stale completion must be harmless
    // because of the production generation token, rather than just our scheduler.
    private static void DrainEvenIfCancelled(Coroutine handle)
    {
        int steps=0;
        while (handle.Routine.MoveNext()) if (++steps>30) throw new Exception("Countdown did not terminate");
    }
    private static Coroutine Latest(CountdownUIGeneric countdown) { return countdown.StartedCoroutines.Last(); }
    private static void InitialAndResumeCountdownSuspension()
    {
        var f=new Fixture(); f.Calibrate(); f.Load(new NoteEvent {midiNumber=60,time=2,duration=1});
        f.Menu.HideAllMenus(); f.Countdown.StartCountdown();
        Check(f.Game.State==RhythmGameManager.PlaybackState.Starting && !f.Game.playing,"Real countdown registers Starting before first frame");
        var initial=Latest(f.Countdown); int starts=f.Countdown.StartedCoroutines.Count;
        f.Countdown.StartCountdown();
        Check(f.Countdown.StartedCoroutines.Count==starts,"Repeated start cannot create a competing countdown");
        Call(f.Game,"OnApplicationPause",true); Call(f.Feedback,"OnApplicationPause",true);
        Check(f.Game.State==RhythmGameManager.PlaybackState.Idle && !f.Game.HasSession,"Suspending initial countdown returns to Idle without a session");
        Check(!f.Countdown.countdown.activeSelf && f.Menu.singlePlayerLobbyMenu.activeSelf,"Cancelled initial countdown hides itself and restores real start lobby");
        DrainEvenIfCancelled(initial);
        Check(!f.Game.playing && !f.Game.HasSession,"Even forcibly drained cancelled initial countdown cannot start playback");
        Call(f.Game,"OnApplicationPause",false); Call(f.Feedback,"OnApplicationPause",false);
        Check(!f.Game.playing,"Foreground return does not start cancelled initial playback");
        f.Countdown.StartCountdown(); DrainEvenIfCancelled(Latest(f.Countdown));
        Check(f.Game.playing && !f.Countdown.countdown.activeSelf,"Fresh real countdown completes into Playing");
        Time.time=1; f.Game.Pause(); f.Menu.HideAllMenus(); f.Countdown.ResumeCountdown();
        Check(f.Game.State==RhythmGameManager.PlaybackState.Resuming && f.Game.HasSession,"Resume countdown retains the paused session");
        double pausedTime=f.Game.SongTime; Time.time=30;
        Near(pausedTime,f.Game.SongTime,"SongTime stays frozen throughout Resuming countdown");
        var resume=Latest(f.Countdown);
        Call(f.Feedback,"OnApplicationPause",true); Call(f.Game,"OnApplicationPause",true);
        Check(f.Game.State==RhythmGameManager.PlaybackState.Paused && f.Game.HasSession,"Suspending resume countdown returns to Paused");
        Check(!f.Countdown.countdown.activeSelf && f.Menu.singleplayerPauseMenu.activeSelf,"Cancelled resume countdown restores real pause menu");
        DrainEvenIfCancelled(resume);
        Check(!f.Game.playing,"Stale cancelled resume callback cannot resume");
        Call(f.Feedback,"OnApplicationPause",false); Call(f.Game,"OnApplicationPause",false);
        Check(!f.Game.playing,"Resume-countdown foreground return requires explicit action");
        f.Countdown.ResumeCountdown(); DrainEvenIfCancelled(Latest(f.Countdown));
        Check(f.Game.State==RhythmGameManager.PlaybackState.Playing,"Fresh real resume countdown works after recovery");
        f.Game.Quit();
    }

    private static void CountdownInvalidation()
    {
        var f=new Fixture(); f.Load(new NoteEvent {midiNumber=60,time=0,duration=1});
        f.Countdown.StartCountdown(); var old=Latest(f.Countdown); f.Game.Quit();
        Check(f.Menu.songMenu.activeSelf && !f.Countdown.countdown.activeSelf,"Quit during countdown recovers the real song menu");
        f.Countdown.StartCountdown(); var current=Latest(f.Countdown);
        DrainEvenIfCancelled(old);
        Check(f.Game.State==RhythmGameManager.PlaybackState.Starting && f.Countdown.countdown.activeSelf,"Stale quit-generation routine cannot complete or hide the new countdown");
        DrainEvenIfCancelled(current); Check(f.Game.playing,"Current generation still starts normally"); f.Game.Quit();
        f.Menu.HideAllMenus(); f.Countdown.StartCountdown(); old=Latest(f.Countdown);
        f.Game.updateSong(new SongData {PartialSongData=new PartialSongData {JsonPath="missing-regression-fixture.json"}});
        DrainEvenIfCancelled(old);
        Check(!f.Game.playing && !f.Game.HasSession && !f.Countdown.countdown.activeSelf,"Replacing a song invalidates pending countdown completion");
        Check(f.Menu.singlePlayerLobbyMenu.activeSelf,"Replacing a song recovers the real start lobby");
        f.Load(new NoteEvent {midiNumber=62,time=0,duration=1});
        f.Countdown.StartCountdown(); old=Latest(f.Countdown); Call(f.Countdown,"OnDisable"); DrainEvenIfCancelled(old);
        Check(!f.Game.playing && !f.Countdown.countdown.activeSelf,"Disabling real countdown cancels its pending generation");
    }

    private static void FocusAndInputGates()
    {
        var f=new Fixture(); f.Calibrate(); f.Load(new NoteEvent {midiNumber=60,time=0,duration=1});
        Send(f.Feedback,"note",60); f.Game.Play();
        Check(((HeldNotes)Field(f.Feedback,"held")).IsHeld(60),"Physical note is held before focus loss");
        Call(f.Game,"OnApplicationFocus",false); Call(f.Feedback,"OnApplicationFocus",false);
        Check(!f.Game.InputAllowed && f.Game.State==RhythmGameManager.PlaybackState.Paused,"Focus loss pauses manager and blocks input");
        Check((bool)Field(f.Feedback,"rangeReady"),"Focus loss preserves completed physical range");
        Check(!((HeldNotes)Field(f.Feedback,"held")).IsHeld(60),"Focus loss clears all held physical keys");
        Time.time=20; Send(f.Feedback,"note",60); Call(f.Feedback,"Update");
        Check(!((HeldNotes)Field(f.Feedback,"held")).IsHeld(60) && f.Matcher.Hits==0 && f.Matcher.Wrong==0 && f.Matcher.Misses==0,"Unfocused notes neither hold keys nor score or expire notes");
        f.Game.Resume(); Check(!f.Game.playing,"Blocked direct Resume cannot bypass lost focus");
        Call(f.Feedback,"OnApplicationFocus",true); Call(f.Game,"OnApplicationFocus",true);
        Check(f.Game.InputAllowed && !f.Game.playing,"Regaining focus allows input without auto-resuming");
        f.Game.Quit();

        foreach (bool pauseFirst in new [] {true,false})
        {
            f=new Fixture();
            if (pauseFirst) { f.Game.SetApplicationPaused(true); f.Game.SetApplicationFocused(false); }
            else { f.Game.SetApplicationFocused(false); f.Game.SetApplicationPaused(true); }
            Check(!f.Game.InputAllowed && !f.Game.CanSelectInstrument,"Both lost lifecycle flags block idle input and selection");
            if (pauseFirst) f.Game.SetApplicationPaused(false); else f.Game.SetApplicationFocused(true);
            Check(!f.Game.InputAllowed,"First recovery callback cannot clear the other lifecycle flag");
            OVRInput.Press(OVRInput.Button.Two,OVRInput.Controller.RTouch); Call(f.Feedback,"Update");
            Send(f.Feedback,"note",36); Send(f.Feedback,"note",96);
            Check(!(bool)Field(f.Feedback,"calibrating") && !(bool)Field(f.Feedback,"rangeReady"),"Calibration cannot begin or finish while either lifecycle gate is blocked");
            f.Selector.selectBongos(); Check(f.Game.IsPiano,"Blocked idle selector cannot change strategy");
            if (pauseFirst) f.Game.SetApplicationFocused(true); else f.Game.SetApplicationPaused(false);
            Check(f.Game.InputAllowed && f.Game.CanSelectInstrument,"Both recovery orders unlock only after both flags recover");
        }
        f=new Fixture(); Call(f.Feedback,"BeginCalibration"); Send(f.Feedback,"note",36);
        Call(f.Feedback,"OnApplicationFocus",false);
        Check(!(bool)Field(f.Feedback,"calibrating"),"Focus loss cancels in-progress calibration");
        Send(f.Feedback,"note",96); Call(f.Feedback,"OnApplicationFocus",true);
        Check(!(bool)Field(f.Feedback,"rangeReady"),"Stale endpoint cannot complete calibration across focus loss");
        f.Load(new NoteEvent {midiNumber=60,time=0,duration=1}); f.Countdown.StartCountdown(); var pending=Latest(f.Countdown);
        Call(f.Game,"OnApplicationFocus",false); Call(f.Feedback,"OnApplicationFocus",false); DrainEvenIfCancelled(pending);
        Check(!f.Game.playing && f.Menu.singlePlayerLobbyMenu.activeSelf,"Focus loss cancels an initial countdown and restores start lobby");
    }

    private static void RealSelectorAndMenu()
    {
        var f=new Fixture(); f.Load(new NoteEvent {midiNumber=60,time=0,duration=1},new NoteEvent {midiNumber=72,time=1,duration=1});
        // UnityEvent listeners can invoke menu navigation before or after selection.
        f.Menu.postInstrumentSelectedMenu(); f.Selector.selectBongos();
        Check(!f.Game.IsPiano && ProjectConfig.Settings.instrument=="bongos" && f.Menu.songMenu.activeSelf,"Menu-first actual bongo selection updates strategy and settings");
        f.Selector.selectPiano(); f.Menu.postInstrumentSelectedMenu();
        Check(f.Game.IsPiano && ProjectConfig.Settings.instrument=="piano" && f.Menu.songMenu.activeSelf,"Selector-first actual piano selection updates strategy and settings");
        f.Menu.HideAllMenus(); f.Countdown.StartCountdown();
        OVRInput.Press(OVRInput.Button.Two,OVRInput.Controller.RTouch); Call(f.Feedback,"Update");
        Check(!(bool)Field(f.Feedback,"calibrating"),"Actual calibration controller input is blocked during Starting countdown");
        f.Menu.postInstrumentSelectedMenu(); f.Selector.selectBongos();
        Check(f.Game.IsPiano && ProjectConfig.Settings.instrument=="piano" && !f.Menu.songMenu.activeSelf,"Menu-first stale selection during Starting cannot navigate or change strategy/settings");
        f.Selector.selectBongos(); f.Menu.postInstrumentSelectedMenu();
        Check(f.Game.IsPiano && !f.Menu.songMenu.activeSelf,"Selector-first stale selection during Starting is also rejected");
        DrainEvenIfCancelled(Latest(f.Countdown));
        int routines=f.Countdown.StartedCoroutines.Count;
        f.Countdown.StartCountdown();
        Check(f.Game.playing && f.Countdown.StartedCoroutines.Count==routines && !f.Menu.singleplayerPauseMenu.activeSelf && !f.Menu.singlePlayerLobbyMenu.activeSelf && !f.Menu.songMenu.activeSelf,"Rejected stale StartCountdown during Playing preserves playback and hidden menus");
        f.Countdown.ResumeCountdown();
        Check(f.Game.playing && f.Countdown.StartedCoroutines.Count==routines && !f.Menu.singleplayerPauseMenu.activeSelf && !f.Menu.singlePlayerLobbyMenu.activeSelf && !f.Menu.songMenu.activeSelf,"Rejected stale ResumeCountdown during Playing preserves playback and hidden menus");
        f.Selector.selectBongos(); f.Menu.postInstrumentSelectedMenu();
        Check(f.Game.IsPiano && !f.Menu.songMenu.activeSelf,"Actual selector and menu reject changes during Playing");
        f.Game.Pause(); f.Menu.ShowSingleplayerPauseMenu(); f.Selector.selectBongos(); f.Menu.postInstrumentSelectedMenu();
        Check(f.Game.IsPiano && f.Menu.singleplayerPauseMenu.activeSelf,"Actual selector and menu reject changes during Paused");
        f.Game.Quit(); f.Selector.selectBongos(); f.Game.Play();
        Check(!f.Game.IsPiano && f.Bongo.average_key==66,"Accepted idle switch uses bongo average calculated for the currently loaded song at Play");
        f.Game.Quit();
    }

    private static void HeadsetFocusRecovery()
    {
        var f=new Fixture(); f.Calibrate(); f.Load(new NoteEvent {midiNumber=60,time=0,duration=1});
        Send(f.Feedback,"note",60); f.Game.Play(); Time.time=1;
        OVRManager.LoseFocus();
        Check(!f.Game.InputAllowed && f.Game.State==RhythmGameManager.PlaybackState.Paused && f.Menu.singleplayerPauseMenu.activeSelf,"Real subscribed OVR focus-loss event pauses playback and restores pause menu");
        Check((bool)Field(f.Feedback,"rangeReady") && (bool)Field(f.Feedback,"scoring") && !((HeldNotes)Field(f.Feedback,"held")).IsHeld(60),"Headset focus-only interruption clears physical keys and preserves completed range and scoring schedule");
        f.Game.SetApplicationFocused(false); f.Game.SetApplicationPaused(true); OVRManager.AcquireFocus();
        Check(!f.Game.InputAllowed,"Headset focus acquisition cannot override app pause and app focus gates");
        f.Game.SetApplicationPaused(false);
        Check(!f.Game.InputAllowed,"App unpause cannot override remaining app focus gate");
        Time.time=100; Send(f.Feedback,"note",60); Call(f.Feedback,"Update");
        Check(f.Matcher.Hits==0 && f.Matcher.Wrong==0 && f.Matcher.Misses==0,"Overlapping lifecycle gates stop scoring and expiry throughout suspension");
        f.Game.SetApplicationFocused(true);
        Check(f.Game.InputAllowed && !f.Game.playing,"All three lifecycle gates must recover, with no automatic resume");
        f.Game.Resume(); Time.time=101; Send(f.Feedback,"note",60);
        Check(f.Matcher.Hits==1,"Explicit focus-only resume preserves scoring and pause-adjusted strike time"); f.Game.Quit();

        f=new Fixture(); f.Load(new NoteEvent {midiNumber=60,time=0,duration=1}); f.Menu.HideAllMenus();
        f.Countdown.StartCountdown(); var initial=Latest(f.Countdown); OVRManager.LoseFocus();
        Check(f.Game.State==RhythmGameManager.PlaybackState.Idle && f.Menu.singlePlayerLobbyMenu.activeSelf && !f.Countdown.countdown.activeSelf,"Headset focus loss cancels real initial countdown and restores start lobby");
        OVRManager.AcquireFocus(); DrainEvenIfCancelled(initial);
        Check(!f.Game.playing,"Headset regain and stale initial coroutine cannot auto-start playback");
        f.Countdown.StartCountdown(); DrainEvenIfCancelled(Latest(f.Countdown)); f.Game.Pause(); f.Menu.HideAllMenus();
        f.Countdown.ResumeCountdown(); var resume=Latest(f.Countdown); OVRManager.LoseFocus();
        Check(f.Game.State==RhythmGameManager.PlaybackState.Paused && f.Menu.singleplayerPauseMenu.activeSelf && !f.Countdown.countdown.activeSelf,"Headset focus loss cancels real resume countdown and restores pause menu");
        OVRManager.AcquireFocus(); DrainEvenIfCancelled(resume);
        Check(!f.Game.playing && f.Game.HasSession,"Headset regain and stale resume coroutine preserve paused session");
        f.Game.Resume(); Call(f.Game,"OnDisable");
        Check(!f.Game.playing && f.Menu.singleplayerPauseMenu.activeSelf,"Disabling manager interrupts playback through the same recovery path");
        f.Game.Quit();
    }

    private static void StartButtonFlow(Fixture f)
    {
        // This is the serialized UnityEvent order, checked independently in MainScene.
        f.Menu.HideAllMenus(); f.Countdown.StartCountdown();
    }
    private static void ResumeButtonFlow(Fixture f)
    {
        f.Menu.HideAllMenus(); f.Countdown.ResumeCountdown();
    }
    private static void RejectedCountdownButtonFlows()
    {
        var f=new Fixture(); f.Menu.ShowSinglePlayerLobbyMenu(); StartButtonFlow(f);
        Check(f.Game.State==RhythmGameManager.PlaybackState.Idle && !f.Countdown.countdown.activeSelf && f.Countdown.StartedCoroutines.Count==0,"Actual ordered Start-button flow cannot launch with missing notes");
        Check(f.Menu.singlePlayerLobbyMenu.activeSelf,"Rejected Start button restores reachable lobby after HideAllMenus listener");

        f=new Fixture(); f.Load(new NoteEvent {midiNumber=60,time=0,duration=1});
        f.Game.updateSong(new SongData {PartialSongData=new PartialSongData {JsonPath="missing-loading-regression-fixture.json"}});
        StartButtonFlow(f);
        Check(f.Game.State==RhythmGameManager.PlaybackState.Idle && !f.Countdown.countdown.activeSelf && f.Countdown.StartedCoroutines.Count==0,"Actual ordered Start-button flow cannot launch while asynchronous song loading has cleared notes");
        Check(f.Menu.singlePlayerLobbyMenu.activeSelf,"Rejected Start during loading keeps start/retry lobby reachable");

        f=new Fixture(); f.Load(new NoteEvent {midiNumber=60,time=0,duration=1}); f.Plane.width=0; StartButtonFlow(f);
        Check(f.Game.State==RhythmGameManager.PlaybackState.Idle && !f.Countdown.countdown.activeSelf && f.Countdown.StartedCoroutines.Count==0,"Actual ordered Start-button flow rejects an instrument that is not ready");
        Check(f.Menu.singlePlayerLobbyMenu.activeSelf,"Instrument-readiness rejection restores lobby after hiding menus");

        f=new Fixture(); f.Load(new NoteEvent {midiNumber=60,time=0,duration=1}); f.Game.SetApplicationPaused(true); StartButtonFlow(f);
        Check(f.Game.State==RhythmGameManager.PlaybackState.Idle && !f.Countdown.countdown.activeSelf && f.Countdown.StartedCoroutines.Count==0,"Actual ordered Start-button flow rejects blocked input");
        Check(f.Menu.singlePlayerLobbyMenu.activeSelf,"Blocked Start restores lobby instead of leaving all menus hidden");

        f=new Fixture(); ResumeButtonFlow(f);
        Check(f.Game.State==RhythmGameManager.PlaybackState.Idle && !f.Countdown.countdown.activeSelf && f.Countdown.StartedCoroutines.Count==0,"Actual ordered Resume-button flow rejects an absent session");
        Check(f.Menu.singlePlayerLobbyMenu.activeSelf,"Invalid Resume restores a reachable start lobby after hiding menus");

        f=new Fixture(); f.Load(new NoteEvent {midiNumber=60,time=0,duration=1}); f.Game.Play(); f.Game.Pause(); f.Game.SetApplicationFocused(false); ResumeButtonFlow(f);
        Check(f.Game.State==RhythmGameManager.PlaybackState.Paused && !f.Countdown.countdown.activeSelf && f.Countdown.StartedCoroutines.Count==0,"Actual ordered Resume-button flow rejects input blocked on a paused session");
        Check(f.Menu.singleplayerPauseMenu.activeSelf,"Blocked Resume restores reachable pause controls after hiding menus");
        f.Game.Quit();
    }

    private static void MultiplayerLifecyclePolicy()
    {
        // Local policy regression only. No network transport or RPC delivery is simulated.
        var f=new Fixture(); f.Calibrate(); f.Load(new NoteEvent {midiNumber=60,time=0,duration=1});
        ProjectConfig.Settings.enableMultiplayer=ProjectConfig.Settings.useMultiplayer=true;
        int pauses=0, resumes=0; RhythmGameManager.OnPause+=()=>pauses++; RhythmGameManager.OnResume+=seconds=>resumes++;
        Send(f.Feedback,"note",60); Time.time=10; f.Game.Play(); Time.time=11;
        Call(f.Game,"OnApplicationFocus",false); Call(f.Feedback,"OnApplicationFocus",false);
        Check(f.Game.State==RhythmGameManager.PlaybackState.Playing && pauses==0 && !f.Game.InputAllowed,"Multiplayer local focus loss blocks input without pausing one peer or emitting OnPause");
        Time.time=13; Send(f.Feedback,"note",60); Call(f.Feedback,"Update");
        Near(1,f.Game.SongTime,"Multiplayer clock continues advancing through local focus loss");
        Check(!((HeldNotes)Field(f.Feedback,"held")).IsHeld(60) && f.Matcher.Hits==0 && f.Matcher.Wrong==0 && f.Matcher.Misses==0,"Unfocused multiplayer MIDI cannot hold keys, score, or advance local scoring");
        Call(f.Feedback,"OnApplicationPause",true); Call(f.Game,"OnApplicationPause",true);
        Check(f.Game.State==RhythmGameManager.PlaybackState.Playing && pauses==0 && !f.Game.InputAllowed,"Multiplayer local app suspension also preserves RPC-coordinated playback state");
        Time.time=14; Call(f.Feedback,"OnApplicationFocus",true); Call(f.Game,"OnApplicationFocus",true);
        Check(!f.Game.InputAllowed && f.Game.playing,"Multiplayer focus return cannot override an outstanding app pause gate");
        Time.time=15; Call(f.Game,"OnApplicationPause",false); Call(f.Feedback,"OnApplicationPause",false);
        Near(3,f.Game.SongTime,"Multiplayer recovery does not introduce a local pause-time clock shift");
        Check(f.Game.InputAllowed && f.Game.playing && pauses==0 && resumes==0,"Multiplayer local recovery needs no Resume and emits no pause/resume events");
        f.Game.Pause(); Time.time=20;
        Near(3,f.Game.SongTime,"Explicit multiplayer Pause still freezes the clock for the existing RPC route");
        Check(f.Game.State==RhythmGameManager.PlaybackState.Paused && pauses==1,"Explicit multiplayer Pause still emits the shared pause event");
        f.Game.Resume();
        Check(f.Game.playing && resumes==1,"Explicit multiplayer Resume still restores playback and emits the shared resume event");
        Time.time=21; Near(4,f.Game.SongTime,"Explicit multiplayer Resume removes only the explicitly paused interval"); f.Game.Quit();

        f=new Fixture(); f.Load(new NoteEvent {midiNumber=60,time=0,duration=1});
        ProjectConfig.Settings.enableMultiplayer=ProjectConfig.Settings.useMultiplayer=true;
        f.Menu.HideAllMenus(); f.Countdown.StartCountdown(); var pending=Latest(f.Countdown); OVRManager.LoseFocus(); DrainEvenIfCancelled(pending);
        Check(f.Game.State==RhythmGameManager.PlaybackState.Idle && !f.Countdown.countdown.activeSelf && f.Menu.multiplayerLobbyMenu.activeSelf,"Multiplayer local lifecycle interruption still cancels a pending initial countdown and recovers lobby");
        OVRManager.AcquireFocus(); f.Game.Play(); f.Game.Pause(); f.Menu.HideAllMenus();
        f.Countdown.ResumeCountdown(); pending=Latest(f.Countdown); OVRManager.LoseFocus(); DrainEvenIfCancelled(pending);
        Check(f.Game.State==RhythmGameManager.PlaybackState.Paused && !f.Countdown.countdown.activeSelf && f.Menu.multiplayerPauseMenu.activeSelf,"Multiplayer local lifecycle interruption still cancels a pending resume countdown and preserves explicit pause");
        f.Game.Quit();
    }

    private static void UnisonScoringIntegration()
    {
        var f=new Fixture(); f.Calibrate();
        f.Load(new NoteEvent {midiNumber=60,time=0,duration=1},new NoteEvent {midiNumber=60,time=0,duration=2},
            new NoteEvent {midiNumber=61,time=0,duration=1},new NoteEvent {midiNumber=60,time=1,duration=1});
        f.Game.Play(); Check(f.Matcher.Total==3,"Real BeginSong collapses unison tracks while preserving chord pitches and later repeated notes");
        Time.time=2; Send(f.Feedback,"note",60); Send(f.Feedback,"note",61); Time.time=3; Send(f.Feedback,"note",60);
        f.Feedback.EndSong(true);
        Check(f.Matcher.Hits==3 && f.Matcher.Misses==0 && f.Matcher.Wrong==0,"One physical strike per unison produces exact integration counters");
        f.Game.Quit();
    }

    public static string RunBundled(NoteEvent[] source)
    {
        assertions=0; var f=new Fixture(); f.Calibrate(); f.Load(source); f.Game.Play();
        Check(source.Length==12537,"Bundled Sandstorm source fixture has expected size");
        Check(f.Matcher.Total==10723 && (int)Field(f.Feedback,"skipped")==815,"Bundled Sandstorm schedules 10,723 distinct in-range onsets and excludes 815 out-of-range entries");
        var strikes=source.Select(n=> { var copy=new NoteEvent {key=n.key,time=n.time}; f.Piano.PreprocessNote(copy); return copy; })
            .Where(n=>n.keyIndex>=36 && n.keyIndex<=96).GroupBy(n=>new {n.keyIndex,n.time}).Select(g=>g.First()).OrderBy(n=>n.time).ThenBy(n=>n.keyIndex);
        foreach (var n in strikes) { Time.time=2+n.time; Send(f.Feedback,"note",n.keyIndex); }
        f.Feedback.EndSong(true);
        Check(f.Matcher.Hits==10723 && f.Matcher.Misses==0 && f.Matcher.Wrong==0,"Bundled Sandstorm one-strike-per-onset run yields exact hit/miss/extra counters");
        f.Game.Quit();
        return "PASS: bundled Sandstorm integration: 12,537 source entries, 10,723 hits, 815 skipped, 0 misses, 0 extras (Unity stubs)";
    }

    public static string Run()
    {
        assertions=0; Application.streamingAssetsPath=System.IO.Path.GetTempPath();
        OptionalMidiInitializationFailure();
        MidiYieldsToPianoDefinition();
        CancelPianoDefinitionThroughMenu();
        CalibrationAndHotplug(); PauseReplayAndTiming(); ZeroDurationCompletion(); LifecycleAndCanonicalPitch();
        InitialAndResumeCountdownSuspension(); CountdownInvalidation(); FocusAndInputGates(); RealSelectorAndMenu(); HeadsetFocusRecovery(); UnisonScoringIntegration();
        RejectedCountdownButtonFlows();
        MultiplayerLifecyclePolicy();
        return "PASS: " + assertions + " Unity integration assertions with stubs (not a real Unity build)";
    }
}
