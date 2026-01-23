using System;
using UnityEngine;

public class PianoStrategy : MonoBehaviour, IGameplayStrategy
{
    [Header("Piano Settings")]
    public PlaneController plane;
    public GameObject noteCubePrefab;
    public float fallTime = 2f;
    public float blockDepth = 0.001f;

    public event Action OnNoteFinished;

    private void Start()
    {
        // Listen for plane definition (moved from Manager)
        PianoManagerInstrumentDefiner.OnPlaneDefined += UpdatePlaneVisuals;
    }

    private void OnDestroy()
    {
        PianoManagerInstrumentDefiner.OnPlaneDefined -= UpdatePlaneVisuals;
    }

    // Interface Implementation
    public float ApproachTime => fallTime;

    public bool IsInstrumentReady()
    {
        // Basic check: has the plane been resized/positioned?
        return plane != null && plane.width > 0;
    }

    public void PreprocessNote(NoteEvent note)
    {
        note.keyIndex = plane.NoteNameToKeyIndex(note.key);
    }

    public void SpawnNote(NoteEvent note, float gameSpeed)
    {
        float keyWidth = plane.GetLocalKeyWidth(note.keyIndex);
        if (keyWidth == 0.0f)
        {
            OnNoteFinished?.Invoke();
            return;
        }
        // --- Exact Logic from original SpawnCube ---
        float velocity = plane.height / fallTime;
        float blockHeight = velocity * note.duration;

        GameObject cube = Instantiate(noteCubePrefab);

        // Position logic
        cube.transform.position = plane.transform.TransformPoint(
            plane.GetLocalKeyPosition(note.keyIndex) +
            Vector3.up * (blockHeight / 2f / plane.height)
        );
        cube.transform.rotation = plane.transform.rotation;

        // Scale logic
        cube.transform.localScale = new Vector3(keyWidth * plane.width, blockHeight, blockDepth);

        // Color logic
        Renderer rend = cube.GetComponent<Renderer>();
        if (rend != null)
            rend.material.color = plane.IsWhiteKey(note.keyIndex) ? Color.white : Color.black;

        // Behavior
        var fall = cube.AddComponent<CubeFallBehavior>(); // Renamed from CubeFall to be generic
        fall.Configure(this, plane, note, fallTime, blockHeight, blockDepth, note.keyIndex, rend);
        RhythmGameManager.OnPause += fall.OnPause;
        RhythmGameManager.OnResume += fall.OnResume;
        RhythmGameManager.OnQuit += fall.OnQuit;
    }

    // Called by the behavior when it's done
    public void NotifyNoteDone()
    {
        OnNoteFinished?.Invoke();
    }

    // --- The Plane Visualization Logic (Moved from Manager) ---
    private void UpdatePlaneVisuals(DefinedPlane definedPlane)
    {
        // --- 1. Define the Perpendicular Plane ---
        // The center of our new plane will be the middle point of the top edge.
        Vector3 distToNewCenter = definedPlane.Plane.normal * ((definedPlane.Corner3 - definedPlane.Corner2) / 2f).magnitude;
        Vector3 center = definedPlane.Center + distToNewCenter;

        // For this computation, we assume the following:
        // C4---C3
        //  |   |
        // C1---C2
        // The normal of our new plane will be parallel to one of the edges of the original plane.
        // This makes it "perpendicular" in the sense that it rises up like a wall from that edge.
        Vector3 perpNormal = (definedPlane.Corner2 - definedPlane.Corner3).normalized;

        // --- 2. Define a Rectangle to Visualize the Plane ---
        // A plane is infinite, so we define a rectangle to draw.
        // The "up" direction for our new rectangle is the normal of the original plane.
        Vector3 rectUpDir = definedPlane.Plane.normal.normalized;

        // The "right" direction for our rectangle is perpendicular to both its up direction and its normal.
        // We can find this with the cross product.
        Vector3 rectRightDir = Vector3.Cross(perpNormal, rectUpDir).normalized;

        // Let's define the size of the rectangle based on the original plane's dimensions.
        float rectHeight = Vector3.Distance(definedPlane.Corner1, definedPlane.Corner4);
        float rectWidth = Vector3.Distance(definedPlane.Corner1, definedPlane.Corner2);

        // Calculate the four corners of the visualization rectangle.
        Vector3[] perpCorners = new Vector3[4];
        perpCorners[0] = center + rectRightDir * rectWidth / 2f + rectUpDir * rectHeight / 2f;
        perpCorners[1] = center - rectRightDir * rectWidth / 2f + rectUpDir * rectHeight / 2f;
        perpCorners[2] = center - rectRightDir * rectWidth / 2f - rectUpDir * rectHeight / 2f;
        perpCorners[3] = center + rectRightDir * rectWidth / 2f - rectUpDir * rectHeight / 2f;

        // --- 3. Draw the Perpendicular Rectangle ---
        //perpendicularLineRenderer.enabled = true;
        //perpendicularLineRenderer.positionCount = 4;
        //perpendicularLineRenderer.SetPositions(perpCorners);
        //perpendicularLineRenderer.loop = true;

        // --- 4. Place the Object ---

        float planeLength = (definedPlane.Corner1 - definedPlane.Corner2).magnitude;
        float planeHeight = planeLength / 2;

        // The object should be placed at the center of the new plane.
        Vector3 edgeCenterPoint = (definedPlane.Corner2 + definedPlane.Corner1) / 2;
        Vector3 position = edgeCenterPoint + definedPlane.Plane.normal * planeHeight / 2;
        // Vector3 position = edgeCenterPoint;

        // The object's "forward" direction should face along the new plane's normal.
        // Its "up" direction should align with the "up" direction of the rectangle we calculated.
        Quaternion rotation = Quaternion.LookRotation(perpNormal, rectUpDir);

        // Instantiate the object with the calculated position and rotation.
        plane.width = planeLength;
        plane.height = planeHeight;
        plane.transform.SetPositionAndRotation(position, rotation);
        plane.reInit();
    }
}