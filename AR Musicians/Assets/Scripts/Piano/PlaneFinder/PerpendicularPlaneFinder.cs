using UnityEngine;

// This component now requires a LineRenderer to draw the perpendicular plane.
[RequireComponent(typeof(LineRenderer))]
public class PerpendicularPlaneFinder : MonoBehaviour
{
    [Header("Settings")]
    public GameObject objectToPlaceOnPlane;
    public Color perpendicularPlaneColor = Color.red;

    private LineRenderer perpendicularLineRenderer;
    private Plane perpendicularPlane;
    void Awake()
    {
        // Get and configure the LineRenderer for the perpendicular plane.
        perpendicularLineRenderer = GetComponent<LineRenderer>();
        perpendicularLineRenderer.material = new Material(Shader.Find("Legacy Shaders/Particles/Alpha Blended Premultiply"));
        perpendicularLineRenderer.startColor = perpendicularLineRenderer.endColor = perpendicularPlaneColor;
        perpendicularLineRenderer.startWidth = perpendicularLineRenderer.endWidth = 0.01f;
        perpendicularLineRenderer.positionCount = 0;
        perpendicularLineRenderer.enabled = false;
    }

    // Subscribe to the event when the script is enabled
    private void OnEnable()
    {
        PianoManager.OnPlaneDefined += HandlePlaneDefined;
    }

    // ALWAYS unsubscribe when the script is disabled or destroyed to prevent memory leaks
    private void OnDisable()
    {
        PianoManager.OnPlaneDefined -= HandlePlaneDefined;
    }

    // This is the event handler method. It will only be called when the event is fired.
    private void HandlePlaneDefined(DefinedPlane definedPlane)
    {
        Debug.Log("PlaneUser has received the plane! Calculating and drawing perpendicular plane.");

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
        if (objectToPlaceOnPlane != null)
        {
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
            PlaneController pianoHUD = objectToPlaceOnPlane.GetComponent<PlaneController>();
            pianoHUD.width = planeLength;
            pianoHUD.height = planeHeight;
            objectToPlaceOnPlane = Instantiate(pianoHUD.gameObject, position, rotation);

        }
    }
}