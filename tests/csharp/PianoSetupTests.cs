// Real setup scripts, fake permission/camera/raycast boundaries. No hardware claim.
using System;
using System.Collections.Generic;
using System.Reflection;
using UnityEngine;
using UnityEngine.Android;
using Meta.XR;

namespace UnityEngine
{
    public class Object
    {
        public static void Destroy(Object value) { }
        public static GameObject Instantiate(GameObject source, Vector3 position, Quaternion rotation)
        { if (source == null) throw new ArgumentNullException(nameof(source)); var value = new GameObject(); value.transform.position = position; return value; }
    }
    public class Component : Object
    {
        public GameObject gameObject;
        public Transform transform => gameObject.transform;
        public T GetComponent<T>() where T : Component => gameObject.GetComponent<T>();
    }
    public class MonoBehaviour : Component { }
    public class GameObject : Object
    {
        public string name;
        public bool activeSelf = true;
        public Transform transform = new Transform();
        private readonly Dictionary<Type, Component> components = new Dictionary<Type, Component>();
        public T AddComponent<T>() where T : Component, new()
        { var value = new T { gameObject = this }; components[typeof(T)] = value; return value; }
        public T GetComponent<T>() where T : Component
        { foreach (var value in components.Values) if (value is T) return (T)value; return null; }
        public void SetActive(bool value) { activeSelf = value; }
        public static GameObject CreatePrimitive(PrimitiveType type)
        { var value = new GameObject(); value.AddComponent<Renderer>(); return value; }
    }
    public class Transform { public Vector3 position, localScale; public Quaternion rotation; }
    public struct Vector3
    {
        public float x, y, z;
        public Vector3(float x, float y, float z) { this.x = x; this.y = y; this.z = z; }
        public static Vector3 one => new Vector3(1,1,1);
        public static Vector3 up => new Vector3(0,1,0);
        public static Vector3 left => new Vector3(-1,0,0);
        public static Vector3 right => new Vector3(1,0,0);
        public float magnitude => (float)Math.Sqrt(x*x+y*y+z*z);
        public Vector3 normalized => magnitude == 0 ? new Vector3() : this / magnitude;
        public static Vector3 operator +(Vector3 a, Vector3 b) => new Vector3(a.x+b.x,a.y+b.y,a.z+b.z);
        public static Vector3 operator -(Vector3 a, Vector3 b) => new Vector3(a.x-b.x,a.y-b.y,a.z-b.z);
        public static Vector3 operator *(float a, Vector3 b) => new Vector3(a*b.x,a*b.y,a*b.z);
        public static Vector3 operator *(Vector3 a, float b) => b*a;
        public static Vector3 operator /(Vector3 a, float b) => new Vector3(a.x/b,a.y/b,a.z/b);
        public static Vector3 Cross(Vector3 a, Vector3 b) => new Vector3(a.y*b.z-a.z*b.y,a.z*b.x-a.x*b.z,a.x*b.y-a.y*b.x);
        public static float Dot(Vector3 a, Vector3 b) => a.x*b.x+a.y*b.y+a.z*b.z;
    }
    public struct Vector2 { public float x,y; public Vector2(float x,float y) { this.x=x; this.y=y; } }
    public struct Vector2Int { public int x,y; public Vector2Int(int x,int y) { this.x=x; this.y=y; } public int this[int i] => i==0?x:y; }
    public struct Ray { }
    public struct Quaternion { public static Quaternion identity => new Quaternion(); public static Quaternion LookRotation(Vector3 v) => new Quaternion(); }
    public struct Plane { public Vector3 normal; public Plane(Vector3 a,Vector3 b,Vector3 c) { normal=Vector3.Cross(b-a,c-a).normalized; } }
    public struct Color { public Color(float r,float g,float b,float a) {} public static Color green,cyan,red; }
    public struct Color32 { }
    public class Shader { public static Shader Find(string name) => new Shader(); }
    public class Material
    {
        public Color color; public Texture mainTexture; public int renderQueue;
        public Material(Shader shader) { }
        public void SetFloat(string name,float value) {} public void SetInt(string name,int value) {}
        public void DisableKeyword(string value) {} public void EnableKeyword(string value) {}
    }
    public class Renderer : Component { public bool enabled; public Material material = new Material(new Shader()); }
    public class LineRenderer : Renderer
    {
        public bool useWorldSpace,loop; public float startWidth,endWidth; public int positionCount;
        public void SetPositions(Vector3[] values) {} public void SetPosition(int index,Vector3 value) {}
    }
    public class Texture : Object { public int width,height; }
    public class Texture2D : Texture
    {
        public Texture2D(int width,int height) { this.width=width; this.height=height; }
        public void SetPixels32(Color32[] values) { if (values.Length!=width*height) throw new ArgumentException(); }
        public void SetPixel(int x,int y,Color color) { if (x<0 || y<0 || x>=width || y>=height) throw new ArgumentException(); }
        public void Apply() { }
    }
    public static class Mathf { public static int FloorToInt(float value) => (int)Math.Floor(value); }
    public class Camera : Component { public static Camera main; }
    public static class Debug { public static void Log(object value) {} public static void LogWarning(object value) {} }
    public class HeaderAttribute : Attribute { public HeaderAttribute(string value) {} }
    public class SerializeField : Attribute { }
    public class RangeAttribute : Attribute { public RangeAttribute(float min, float max) {} }
    public class RequireComponent : Attribute { public RequireComponent(Type type) {} }
    public enum PrimitiveType { Quad }
}
namespace UnityEngine.Rendering { public enum BlendMode { SrcAlpha, OneMinusSrcAlpha } public enum RenderQueue { Transparent=3000 } }
namespace UnityEngine.Android
{
    public static class Permission
    {
        public static readonly HashSet<string> Allowed = new HashSet<string>();
        public static readonly List<string> Requests = new List<string>();
        public static bool HasUserAuthorizedPermission(string name) => Allowed.Contains(name);
        public static void RequestUserPermissions(string[] names) { Requests.AddRange(names); }
    }
}
namespace TMPro { public class TMP_Text : Component { public string text; } }
namespace Assets.Scripts { public class NamespaceMarker {} }
namespace Meta.XR.MRUtilityKit { public class NamespaceMarker {} }
namespace NUnit.Framework { public class NamespaceMarker {} }
namespace Meta.XR
{
    public enum EnvironmentRaycastHitStatus { Hit, NotReady, NotSupported, NoHit }
    public struct EnvironmentRaycastHit { public EnvironmentRaycastHitStatus status; public Vector3 point; }
    public class EnvironmentRaycastManager : Component
    {
        public static bool IsSupported = true;
        public readonly Queue<EnvironmentRaycastHit> Results = new Queue<EnvironmentRaycastHit>();
        public int Calls;
        public bool Raycast(Ray ray,out EnvironmentRaycastHit hit)
        { Calls++; hit = Results.Count>0 ? Results.Dequeue() : new EnvironmentRaycastHit { status=EnvironmentRaycastHitStatus.NoHit }; return hit.status==EnvironmentRaycastHitStatus.Hit; }
    }
    public class CameraColors { public Color32[] ToArray() => new Color32[64]; }
    public class PassthroughCameraAccess : Component
    {
        public bool IsPlaying = true;
        public Vector2Int CurrentResolution = new Vector2Int(8,8);
        public CameraColors GetColors() => new CameraColors();
        public Texture GetTexture() => new Texture2D(8,8);
        public Ray ViewportPointToRay(Vector2 point) => new Ray();
    }
}
public static class OVRPermissionsRequester
{
    public const string ScenePermission="com.oculus.permission.USE_SCENE";
    public const string PassthroughCameraAccessPermission="horizonos.permission.HEADSET_CAMERA";
}
public static class OVRInput
{
    public enum Button { One }
    public enum Controller { LTouch,RTouch }
    private static Controller? pending;
    public static void Press(Controller controller) { pending=controller; }
    public static bool GetDown(Button button,Controller controller)
    { if (pending==controller) { pending=null; return true; } return false; }
}
public class ModelManager : MonoBehaviour
{
    public int Calls;
    public Vector2[] Keypoints = { new Vector2(1,1),new Vector2(6,1),new Vector2(6,6),new Vector2(1,6) };
    public float[] Confidences = { 0.9f, 0.9f, 0.9f, 0.9f };
    public Vector2[] RunInference(Texture texture,int count,out float[] confidences)
    { Calls++; confidences=Confidences; return Keypoints; }
}
public interface IPointInputListener { bool IsActive { get; } void RegisterPoint(Vector3 position); }
public class PlaneCorrectionNodeGeneric : Component { public PianoManagerInstrumentDefiner manager; public int vertexIndex; }

public static class PianoSetupTests
{
    private static int checks;
    private static void Check(bool value,string message) { checks++; if (!value) throw new Exception(message); }
    private static FieldInfo Field(Type type,string name)
    { while (type!=null) { var field=type.GetField(name,BindingFlags.Instance|BindingFlags.NonPublic|BindingFlags.Public|BindingFlags.DeclaredOnly); if (field!=null) return field; type=type.BaseType; } throw new Exception("Missing field "+name); }
    private static void Set(object target,string field,object value) => Field(target.GetType(),field).SetValue(target,value);
    private static void Call(object target,string method) => target.GetType().GetMethod(method,BindingFlags.Instance|BindingFlags.Public|BindingFlags.NonPublic).Invoke(target,null);
    private static int PointCount(PianoManagerInstrumentDefiner manager) => ((List<Vector3>)Field(manager.GetType(),"capturedPoints").GetValue(manager)).Count;
    private static readonly Vector3[] Corners = {new Vector3(0,0,0),new Vector3(1,0,0),new Vector3(1,0,0.2f)};

    private static void ValidateDetectionQuality()
    {
        const float cutoff = PianoDetectionValidator.DefaultMinimumConfidence;
        var scores = new[] { 0.3f, 0.6f, 0.8f, 0.7f };
        var corners = new[] { new Vector2(47,132), new Vector2(280,116), new Vector2(290,141), new Vector2(28,165) };
        Check(PianoDetectionValidator.Validate(corners, scores, 320, 240, cutoff) == PianoDetectionIssue.None,
            "A usable perspective view should permit spatial raycasts");
        Check(PianoDetectionValidator.Validate(corners, null, 320, 240, cutoff) == PianoDetectionIssue.MissingCorners,
            "Missing confidence output should fail safely");

        // Actual recorded Quest corner output from the 1.1.1 setup investigation.
        var captured = new[] { new Vector2(47,132), new Vector2(301,116), new Vector2(319,141), new Vector2(28,165) };
        var capturedScores = new[] { 0.301474f, 0.592472f, 0.031148f, 0.637529f };
        Check(PianoDetectionValidator.Validate(captured, capturedScores, 320, 240, cutoff) == PianoDetectionIssue.ClippedCorner,
            "A corner on the rightmost pixel should ask the player to reframe");
        captured[2] = new Vector2(318,141);
        Check(PianoDetectionValidator.Validate(captured, capturedScores, 320, 240, cutoff) == PianoDetectionIssue.LowConfidence,
            "A low-confidence corner inside the frame should also prevent confirmation");

        var invalid = (Vector2[])corners.Clone();
        invalid[3] = new Vector2(float.NaN,165);
        Check(PianoDetectionValidator.Validate(invalid, scores, 320, 240, cutoff) == PianoDetectionIssue.InvalidCorner,
            "The fourth corner must have finite coordinates");
        invalid[3] = new Vector2(28,float.PositiveInfinity);
        Check(PianoDetectionValidator.Validate(invalid, scores, 320, 240, cutoff) == PianoDetectionIssue.InvalidCorner,
            "Infinite coordinates must be rejected");
        invalid[3] = corners[2];
        Check(PianoDetectionValidator.Validate(invalid, scores, 320, 240, cutoff) == PianoDetectionIssue.InvalidGeometry,
            "Duplicate corners must be rejected");
        invalid[2] = corners[3];
        Check(PianoDetectionValidator.Validate(invalid, scores, 320, 240, cutoff) == PianoDetectionIssue.InvalidGeometry,
            "Crossed edges must be rejected");
        invalid[2] = corners[2];
        invalid[3] = new Vector2(180,133);
        Check(PianoDetectionValidator.Validate(invalid, scores, 320, 240, cutoff) == PianoDetectionIssue.InvalidGeometry,
            "A fourth corner inside the other three must be rejected");
        invalid = new[] { new Vector2(12,12), new Vector2(14,12), new Vector2(14,13), new Vector2(12,13) };
        Check(PianoDetectionValidator.Validate(invalid, scores, 320, 240, cutoff) == PianoDetectionIssue.InvalidGeometry,
            "A tiny polygon without coincident corners must still be rejected");
        scores[3] = float.NaN;
        Check(PianoDetectionValidator.Validate(corners, scores, 320, 240, cutoff) == PianoDetectionIssue.LowConfidence,
            "A nonfinite confidence must fail safely");
    }

    private sealed class Rig
    {
        public PianoManagerInstrumentDefiner Manager;
        public CVPlaneFinderGeneric CV;
        public EnvironmentRaycastManager Rays;
        public PassthroughCameraAccess Camera;
        public ModelManager Model;
        public TMPro.TMP_Text Text;
        public Rig()
        {
            var go=new GameObject(); go.AddComponent<LineRenderer>(); Manager=go.AddComponent<PianoManagerInstrumentDefiner>();
            Set(Manager,"pointPrefab",new GameObject()); Set(Manager,"planeCornerCorrectorNode",new GameObject()); Call(Manager,"Awake");
            CV=new GameObject().AddComponent<CVPlaneFinderGeneric>(); Rays=new GameObject().AddComponent<EnvironmentRaycastManager>();
            Camera=new GameObject().AddComponent<PassthroughCameraAccess>(); Model=new GameObject().AddComponent<ModelManager>(); Text=new GameObject().AddComponent<TMPro.TMP_Text>();
            Set(CV,"manager",Manager); Set(CV,"cameraAccess",Camera); Set(CV,"modelManager",Model); Set(CV,"statusText",Text);
            CV.raycastManager=Rays; Set(Manager,"cvPlaneFinder",CV);
        }
        public void HitAll()
        { Rays.Results.Clear(); foreach (var point in Corners) Rays.Results.Enqueue(new EnvironmentRaycastHit { status=EnvironmentRaycastHitStatus.Hit,point=point }); }
        public void Capture() { OVRInput.Press(OVRInput.Controller.LTouch); Call(CV,"Update"); }
        public void Confirm() { OVRInput.Press(OVRInput.Controller.RTouch); Call(Manager,"Update"); }
    }
    public static void Run()
    {
        ValidateDetectionQuality();
        Permission.Allowed.Clear(); Permission.Requests.Clear();
        var rig=new Rig(); rig.Manager.Activate(true);
        Check(rig.Manager.IsDefining,"CV setup must be defining immediately on activation");
        Check(Permission.Requests.Contains(OVRPermissionsRequester.ScenePermission),"CV activation must request spatial permission");
        Check(Permission.Requests.Contains(OVRPermissionsRequester.PassthroughCameraAccessPermission),"CV activation must request camera permission");
        Check(!rig.Manager.IsActive,"CV must disable manual listener input");
        Check(rig.Text.text.Contains("Camera and spatial"),"Both missing permissions must be visible");
        rig.Capture(); Check(rig.Model.Calls==0 && !rig.Manager.CanConfirm,"Missing permissions must block inference/confirmation");
        Permission.Allowed.Add(OVRPermissionsRequester.PassthroughCameraAccessPermission);
        Call(rig.CV,"Update"); Check(rig.Text.text.Contains("Spatial data permission"),"Spatial-only denial must be visible");
        Permission.Allowed.Remove(OVRPermissionsRequester.PassthroughCameraAccessPermission); Permission.Allowed.Add(OVRPermissionsRequester.ScenePermission);
        Call(rig.CV,"Update"); Check(rig.Text.text.Contains("Camera permission"),"Camera-only denial must be visible");
        Permission.Allowed.Add(OVRPermissionsRequester.PassthroughCameraAccessPermission);
        rig.Camera.IsPlaying=false; rig.Capture(); Check(rig.Text.text.Contains("camera feed") && rig.Model.Calls==0,"Camera startup must be actionable without inference");
        rig.Camera.IsPlaying=true; Call(rig.CV,"Update"); Check(rig.Text.text.Contains("Press X"),"Grant/startup must recover without mode re-entry");
        EnvironmentRaycastManager.IsSupported=false; rig.Capture(); Check(rig.Model.Calls==0 && rig.Text.text.Contains("not supported"),"Unsupported raycast must not wait forever or run inference");
        EnvironmentRaycastManager.IsSupported=true;
        rig.Manager.RegisterPoint(Corners[0]); Check(PointCount(rig.Manager)==0,"Direct manual input cannot enter CV capture");
        int events=0; Action<DefinedPlane> handler=p=>{events++; Check(!rig.Manager.CanConfirm,"Event must observe completed confirmation state");};
        PianoManagerInstrumentDefiner.OnPlaneDefined+=handler;
        try
        {
            rig.Confirm(); Check(events==0,"A before three hits cannot confirm");
            rig.Rays.Results.Enqueue(new EnvironmentRaycastHit { status=EnvironmentRaycastHitStatus.NotReady });
            rig.Capture(); Check(PointCount(rig.Manager)==0 && !rig.Manager.CanConfirm,"NotReady must leave no captured points");
            Check(rig.Text.text.Contains("Spatial raycast is not ready"),"NotReady must explain spatial state");
            rig.Rays.Results.Clear(); rig.Rays.Results.Enqueue(new EnvironmentRaycastHit { status=EnvironmentRaycastHitStatus.Hit,point=Corners[0] });
            rig.Rays.Results.Enqueue(new EnvironmentRaycastHit { status=EnvironmentRaycastHitStatus.NoHit });
            rig.Capture(); Check(PointCount(rig.Manager)==0 && !rig.Manager.CanConfirm,"Partial raycast must never commit first point");
            Check(rig.Manager.IsDefining,"Incomplete detection must keep definition active for retry");
            Check(rig.Text.text.Contains("Incomplete keyboard detection"),"Partial detection must show retry guidance");
            rig.Confirm(); Check(events==0,"A after partial failure cannot confirm");
            rig.HitAll(); rig.Capture(); Check(PointCount(rig.Manager)==3 && rig.Manager.CanConfirm,"X retry with three hits must create one complete candidate");
            Check(rig.Manager.IsDefining,"Ready candidate must remain defining until confirmation");
            Check(!rig.Manager.IsActive && rig.Text.text.Contains("Press A on the right"),"Complete candidate must guide A right while keeping manual capture off");
            rig.Manager.RegisterPoint(Corners[0]); Check(PointCount(rig.Manager)==3,"Extra manual points cannot mutate candidate");
            rig.Rays.Results.Clear(); rig.Rays.Results.Enqueue(new EnvironmentRaycastHit { status=EnvironmentRaycastHitStatus.NoHit });
            rig.Capture(); Check(PointCount(rig.Manager)==0 && !rig.Manager.CanConfirm,"Failed X retry must clear previous candidate");
            rig.Confirm(); Check(events==0,"A cannot confirm stale previous candidate after retry failed");
            rig.CV.parseRays(null); Check(!rig.Manager.CanConfirm && rig.Text.text.Contains("three corner rays"),"Null rays must fail safely");
            rig.CV.parseRays(new Ray[2]); Check(PointCount(rig.Manager)==0,"Wrong ray count must commit nothing");
            rig.CV.raycastManager=null; rig.CV.parseRays(new Ray[3]); Check(!rig.Manager.CanConfirm && rig.Text.text.Contains("setup is missing"),"Missing raycaster must fail safely");
            rig.CV.raycastManager=rig.Rays;
            Check(!rig.Manager.TryRegisterDetectedPoints(new[] {Corners[0],Corners[0],Corners[0]}),"Collapsed hit positions must be rejected");
            Check(!rig.Manager.TryRegisterDetectedPoints(new[] {Corners[0],Corners[1],new Vector3(float.NaN,0,0)}),"Nonfinite positions must be rejected");
            rig.Model.Keypoints=null; rig.Capture(); Check(!rig.Manager.CanConfirm && rig.Text.text.Contains("All four corners"),"Null inference must fail safely");
            rig.Model.Keypoints=new[] {new Vector2(-1,1),new Vector2(1,1),new Vector2(1,2),new Vector2(0,2)};
            rig.Capture(); Check(!rig.Manager.CanConfirm && rig.Text.text.Contains("outside the camera image"),"Invalid image corner must not form geometry");
            rig.Model.Keypoints=new[] {new Vector2(1,1),new Vector2(6,1),new Vector2(6,6),new Vector2(1,6)};
            rig.Model.Confidences[3]=0.03f;
            int rayCount=rig.Rays.Calls;
            rig.Capture(); Check(!rig.Manager.CanConfirm && rig.Rays.Calls==rayCount && rig.Text.text.Contains("located reliably"),
                "The fourth corner must pass the image gate even though it is not used for raycasts");
            AppText.SetLanguage(AppLanguage.Spanish);
            Check(rig.CV.StatusMessage.Contains("Aléjate") && rig.CV.StatusMessage.Contains("configuración manual"),
                "The current status must reflect a language change without another capture");
            rig.CV.CaptureKeyboard(); Check(rig.Text.text.Contains("Aléjate") && rig.Text.text.Contains("configuración manual"),
                "The rejection must explain how to retry or use manual setup in Spanish");
            AppText.SetLanguage(AppLanguage.English);
            rig.Model.Confidences[3]=0.9f;
            rig.HitAll(); rig.Capture(); rig.Confirm();
            Check(events==1 && !rig.Manager.CanConfirm && !rig.Manager.IsActive && !rig.CV.active,"A right must confirm exactly once and stop capture");
            Check(!rig.Manager.IsDefining,"Confirmation must end definition so gameplay input can resume");
            rig.Confirm(); Call(rig.Manager,"ConfirmFinalPlane"); Check(events==1,"Repeated confirm must not emit another event");
            rig.HitAll(); rig.Capture(); Check(events==1 && rig.Rays.Results.Count==3,"X after confirm must not silently restart setup");
            rig.Manager.Activate(true); rig.HitAll(); rig.Capture(); Check(rig.Manager.CanConfirm,"Explicit new setup must permit a new detection");
            rig.Manager.Deactivate(); Call(rig.Manager,"ConfirmFinalPlane"); Check(events==1 && !rig.Manager.CanConfirm,"Deactivation must revoke pending confirmation");
            Check(!rig.Manager.IsDefining,"Deactivation must end definition");
            Check(PointCount(rig.Manager)==0,"Cancel after three CV points must clear the unfinished candidate");
        }
        finally { PianoManagerInstrumentDefiner.OnPlaneDefined-=handler; }
        rig.Manager.Activate(false); Check(rig.Manager.IsActive && !rig.CV.active,"Manual activation must disable CV");
        Check(rig.Manager.IsDefining,"Manual setup must also expose defining state");
        rig.Manager.RegisterPoint(Corners[0]); rig.Manager.RegisterPoint(Corners[1]);
        rig.Manager.Deactivate();
        Check(!rig.Manager.CanConfirm && PointCount(rig.Manager)==0,"Cancel after two manual points must clear partial setup");
        Check(((List<GameObject>)Field(rig.Manager.GetType(),"spawnedVisuals").GetValue(rig.Manager)).Count==0,"Manual cancel must clear tracked point visuals");
        Check(rig.Manager.GetComponent<LineRenderer>().positionCount==0,"Cancel must clear unfinished outline");
        rig.Manager.Activate(false);
        Check(rig.Manager.IsDefining && rig.Manager.IsActive && PointCount(rig.Manager)==0,"Manual reactivation must begin cleanly");
        foreach (var point in Corners) rig.Manager.RegisterPoint(point);
        Check(rig.Manager.CanConfirm && !rig.Manager.IsActive,"Manual three-point setup must still reach correction mode");
        Check(rig.Manager.IsDefining,"Manual correction must remain defining until confirmation");
        Call(rig.Manager,"ConfirmFinalPlane");
        rig.Manager.Deactivate();
        Check(PointCount(rig.Manager)==3 && rig.Manager.GetComponent<LineRenderer>().positionCount==4,"Leaving after confirmation must preserve the confirmed surface");
        Console.WriteLine("Piano setup regression: "+checks+" checks passed.");
    }
}
