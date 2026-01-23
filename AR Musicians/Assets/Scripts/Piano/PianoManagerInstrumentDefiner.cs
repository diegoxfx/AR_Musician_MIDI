using Assets.Scripts;
using Meta.XR;
using Meta.XR.MRUtilityKit;
using System;
using System.Collections.Generic;
using UnityEngine;

// Now inherits from InstrumentDefiner to support generic RayCastInputProvider
[RequireComponent(typeof(LineRenderer))]
public class PianoManagerInstrumentDefiner : InstrumentDefiner
{
    #region Serialized members
    [Header("Piano Specific Settings")]
    // We map the old prefab logic to the base class 'pointPrefab' in Awake if needed, 
    // or you can just assign 'pointPrefab' in the inspector directly.

    // TODO: Move to PlaneController class for better abstraction
    [SerializeField] private GameObject planeCornerCorrectorNode;

    [Header("Visuals")]
    [SerializeField] private Color userPlaneColor = Color.green;
    [SerializeField] private Color mathPlaneColor = new Color(0.0f, 0.5f, 1.0f, 0.5f); // Blue with transparency
    [SerializeField] private float mathPlaneSize = 0.5f;

    [Header("Plane Detectors")]
    // RayCastPlaneFinder is removed because we now use the generic RayCastInputProvider
    [SerializeField] private CVPlaneFinderGeneric cvPlaneFinder;

    #endregion

    #region Events
    public static event Action<DefinedPlane> OnPlaneDefined;
    #endregion

    #region Private members
    // Materials - cached for performance
    private Material userPlaneMaterial;
    private Material mathPlaneMaterial;

    // State variables
    private bool inCorrectionMode = false; // New state: after 3 points, before final confirm
    private float sideLength = 0.0f;


    // Piano specific arrays that the correctors need
    private Vector3[] planeAnchors; // Stores the 4 corrected corners
    private GameObject[] anchorPrefabs; // Stores the visual objects for corners

    // Correction Nodes
    private GameObject leftCornerCorrecorNode;
    private GameObject rightCornerCorrectorNode;
    private GameObject mathPlaneVisualizer;
    private LinkedList<GameObject> setupGameObjects;
    #endregion

    protected override void Awake()
    {
        base.Awake(); // Call InstrumentDefiner's Awake to setup LineRenderer

        // --- Initialize collections and arrays ---
        // We use 'capturedPoints' from base class for the first 3 input points.
        // We use 'planeAnchors' for the final 4 corners (including the calculated 4th).
        planeAnchors = new Vector3[4];
        anchorPrefabs = new GameObject[4];

        // --- Configure LineRenderer ---
        // Overwrite base class settings if specific piano style is needed
        lineRenderer.loop = true;

        // --- Create and cache materials ---
        userPlaneMaterial = new Material(Shader.Find("Sprites/Default"));
        userPlaneMaterial.color = userPlaneColor;
        lineRenderer.material = userPlaneMaterial;

        mathPlaneMaterial = new Material(Shader.Find("Standard"));
        mathPlaneMaterial.color = mathPlaneColor;
        SetupTransparentMaterial(mathPlaneMaterial);
    }

    public void Activate(bool automatic)
    {
        IsActive = true;
        // Reset local piano state
        ResetDefinition();

        Debug.Log("Starting the Piano Manager..");

        if (automatic)
        {
            Debug.Log("Piano Manager activated in CV mode.");
            // If CV, we bypass the generic input listener logic
            if (cvPlaneFinder != null)
                cvPlaneFinder.Activate();
            else
                Debug.Log("cvPlaneFinder was null...");
        }
        else
        {
            // If Manual, we activate the base class logic.
            // This sets IsActive = true, allowing RayCastInputProvider to call RegisterPoint.
            base.Activate();
            Debug.Log("Piano Manager Activated in Manual Mode via InstrumentDefiner");
        }
    }

    public void Deactivate()
    {
        if (cvPlaneFinder != null) cvPlaneFinder.Deactivate();
        if (leftCornerCorrecorNode != null) leftCornerCorrecorNode.SetActive(false);
        if (rightCornerCorrectorNode != null) rightCornerCorrectorNode.SetActive(false);
        IsActive = false;
    }

    private void Update()
    {
        // Specific Piano Logic:
        // After defining the 3 points, we enter "Correction Mode".
        // The user must press "A" (Button One) to confirm the final shape.
        if (inCorrectionMode && OVRInput.GetDown(OVRInput.Button.One, OVRInput.Controller.RTouch))
        {
            ConfirmFinalPlane();
        }
    }

    // --- INSTRUMENT DEFINER OVERRIDES ---

    protected override void UpdateDrawing()
    {
        // While capturing the first 3 points, visualize the line connecting them.
        if (!inCorrectionMode)
        {
            lineRenderer.positionCount = capturedPoints.Count;
            lineRenderer.SetPositions(capturedPoints.ToArray());
            lineRenderer.enabled = true;
        }
        // If in correction mode, visuals are handled by UpdateVisuals()
    }

    /// <summary>
    /// Called automatically by InstrumentDefiner when capturedPoints.Count >= requiredPoints (3).

    /// </summary>
    protected override void FinalizeDefinition()
    {
        Debug.Log("3 Points Captured. Calculating 4th point and entering Correction Mode.");

        // 1. Map captured points to the internal anchor logic
        // We assume: P0=TopLeft, P1=TopRight, P2=BottomRight (approx)

        // Plane heuristic: all points lie on same y-value (average of top two)
        float yValue = (capturedPoints[0] + capturedPoints[1]).y / 2;

        planeAnchors[0] = capturedPoints[0];
        planeAnchors[0].y = yValue;
        planeAnchors[1] = capturedPoints[1];
        planeAnchors[1].y = yValue;

        // 2. Calculate the "side length" based on the user's 3rd click
        // Distance from P1 (Top Right) to P2 (Bottom Right approximation)
        sideLength = (capturedPoints[2] - capturedPoints[1]).magnitude;

        // 3. Calculate 3rd and 4th points mathematically (Perpendicular)
        SetBottomCorners();

        // 4. Setup Visuals for Correction Mode
        // We need to transfer the visuals from the base class (spawnedVisuals) 
        // to our local management so we can move them with correctors.

        // Clear base class visuals (we will recreate them as movable anchors)
        foreach (var obj in spawnedVisuals) Destroy(obj);
        spawnedVisuals.Clear();

        // Create new anchors for all 4 corners
        for (int i = 0; i < 4; i++)
        {
            VisualizeAnchor(i);
        }

        // 5. Add Correctors
        AddPlaneCorrectors();

        // 6. Update Line Renderer to show full quad
        UpdateVisuals();

        // 7. Stop accepting generic points, start accepting "A" button confirmation
        IsActive = false; // Stop RayCastInputProvider from adding more points
        inCorrectionMode = true;

        // Update LineRenderer to show the closed loop of 4 points
        lineRenderer.positionCount = 4;
        lineRenderer.loop = true;
    }

    public override void ResetDefinition()
    {
        base.ResetDefinition(); // Clears capturedPoints, lines, base visuals

        inCorrectionMode = false;

        // Clear Piano-specific objects
        if (anchorPrefabs != null)
        {
            for (int i = 0; i < anchorPrefabs.Length; i++)
            {
                if (anchorPrefabs[i] != null) Destroy(anchorPrefabs[i]);
            }
        }

        if (leftCornerCorrecorNode != null) Destroy(leftCornerCorrecorNode);
        if (rightCornerCorrectorNode != null) Destroy(rightCornerCorrectorNode);
        if (mathPlaneVisualizer != null) Destroy(mathPlaneVisualizer);

        if (setupGameObjects != null)
        {
            foreach (GameObject obj in setupGameObjects) if (obj != null) Destroy(obj);
            setupGameObjects.Clear();
        }

        Debug.Log("Piano definition reset.");
    }

    // --- PIANO SPECIFIC LOGIC ---

    private void SetBottomCorners()
    {
        Vector3 baseLine = planeAnchors[1] - planeAnchors[0];
        Vector3 edgeVector = sideLength * Vector3.Cross(baseLine, Vector3.up).normalized;
        planeAnchors[2] = planeAnchors[1] - edgeVector;
        planeAnchors[3] = planeAnchors[0] - edgeVector;
    }

    private void VisualizeAnchor(int index)
    {
        // Use the prefab from the base class (InstrumentDefiner.pointPrefab)
        if (pointPrefab != null)
        {
            GameObject anchor = Instantiate(pointPrefab, planeAnchors[index], Quaternion.identity);
            // We store it in our specific array to move it later
            anchorPrefabs[index] = anchor;
            anchor.transform.localScale = Vector3.one * pointSize;
            // Also track in generic list for cleanup
            if (setupGameObjects == null) setupGameObjects = new LinkedList<GameObject>();
            setupGameObjects.AddLast(anchor);
        }
    }

    private void UpdateVisuals()
    {
        // Update Line Renderer
        lineRenderer.positionCount = 4;
        for (int i = 0; i < 4; i++)
        {
            lineRenderer.SetPosition(i, planeAnchors[i]);
            if (anchorPrefabs[i] != null)
            {
                anchorPrefabs[i].transform.position = planeAnchors[i];
            }
        }
    }

    /// <summary>
    /// Function called by the corrector nodes to update the positions
    /// </summary>
    public void MovePoint(int index, Vector3 delta)
    {
        // Update the specific anchor
        planeAnchors[index] += delta;
        SetBottomCorners();
        UpdateVisuals();
    }

    private void AddPlaneCorrectors()
    {
        // Left Anchor
        Vector3 leftPos = planeAnchors[0] + 0.1f * (Vector3.up + Vector3.left);
        leftCornerCorrecorNode = Instantiate(planeCornerCorrectorNode, leftPos, Quaternion.identity);
        SetupCorrector(leftCornerCorrecorNode, 0);

        // Right Anchor
        Vector3 rightPos = planeAnchors[1] + 0.1f * (Vector3.up + Vector3.right);
        rightCornerCorrectorNode = Instantiate(planeCornerCorrectorNode, rightPos, Quaternion.identity);
        SetupCorrector(rightCornerCorrectorNode, 1);
    }

    private void SetupCorrector(GameObject obj, int index)
    {
        if (setupGameObjects == null) setupGameObjects = new LinkedList<GameObject>();
        setupGameObjects.AddLast(obj);

        if (obj.GetComponent<PlaneCorrectionNodeGeneric>() == null)
        {
            obj.AddComponent<PlaneCorrectionNodeGeneric>();
        }
        PlaneCorrectionNodeGeneric node = obj.GetComponent<PlaneCorrectionNodeGeneric>();
        node.manager = this;
        node.vertexIndex = index;
    }

    /// <summary>
    /// The final step: "Printing" the plane after correction is done.
    /// </summary>
    private void ConfirmFinalPlane()
    {
        // --- Calculate the mathematical plane ---
        Plane initialPlane = new Plane(planeAnchors[0], planeAnchors[1], planeAnchors[2]);

        // --- Ensure the plane's normal is oriented towards the player ---
        Vector3 planeCenter = (planeAnchors[0] + planeAnchors[1] + planeAnchors[2] + planeAnchors[3]) / 4f;
        Vector3 directionToPlayer = Camera.main.transform.position - planeCenter;

        if (Vector3.Dot(initialPlane.normal, directionToPlayer) < 0)
        {
            initialPlane = new Plane(planeAnchors[0], planeAnchors[2], planeAnchors[1]);
        }

        DefinedPlane finalPlane = new DefinedPlane(initialPlane, planeAnchors[0], planeAnchors[1], planeAnchors[2], planeAnchors[3]);

        DrawFinalPlaneVisuals(finalPlane);
        OnPlaneDefined?.Invoke(finalPlane);

        inCorrectionMode = false;
        Debug.Log("Piano Plane Confirmed.");
    }

    private void DrawFinalPlaneVisuals(DefinedPlane definedPlane)
    {
        // 1. Line Renderer
        lineRenderer.SetPositions(planeAnchors);

        // 2. Math Plane
        if (mathPlaneVisualizer != null) Destroy(mathPlaneVisualizer);

        mathPlaneVisualizer = GameObject.CreatePrimitive(PrimitiveType.Quad);
        mathPlaneVisualizer.name = "MathPlaneVisualizer";
        mathPlaneVisualizer.transform.position = definedPlane.Center;
        mathPlaneVisualizer.transform.rotation = Quaternion.LookRotation(definedPlane.Plane.normal);
        mathPlaneVisualizer.transform.localScale = Vector3.one * mathPlaneSize;

        Renderer mathPlaneRenderer = mathPlaneVisualizer.GetComponent<Renderer>();
        if (mathPlaneRenderer != null)
        {
            mathPlaneRenderer.material = mathPlaneMaterial;
        }
    }

    private void SetupTransparentMaterial(Material material)
    {
        material.SetFloat("_Mode", 3);
        material.SetInt("_SrcBlend", (int)UnityEngine.Rendering.BlendMode.SrcAlpha);
        material.SetInt("_DstBlend", (int)UnityEngine.Rendering.BlendMode.OneMinusSrcAlpha);
        material.SetInt("_ZWrite", 0);
        material.DisableKeyword("_ALPHATEST_ON");
        material.EnableKeyword("_ALPHABLEND_ON");
        material.DisableKeyword("_ALPHAPREMULTIPLY_ON");
        material.renderQueue = (int)UnityEngine.Rendering.RenderQueue.Transparent;
    }
}