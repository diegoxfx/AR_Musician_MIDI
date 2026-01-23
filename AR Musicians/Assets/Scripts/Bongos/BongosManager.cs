using System;
using System.Collections.Generic;
using System.Runtime.CompilerServices;
using UnityEngine;

public class BongosManager : InstrumentDefiner
{
    public enum SetupPhase
    {
        DefiningLeftHembra,
        DefiningRightMacho,
        Finished
    }

    [Header("Bongo Settings")]
    [SerializeField] private int circleResolution = 50;
    [SerializeField] private float drumHeight = 0.15f;
    [SerializeField] private Material lineMaterial; // Assign a basic sprite/particle material here

    [Header("Visual Feedback")]
    [SerializeField] private Color leftDrumColor = Color.cyan;
    [SerializeField] private Color rightDrumColor = Color.magenta;

    [Header("Circle Detectors")]
    [SerializeField] private CVCircleFinder cvCircleFinder;

    // Event to notify game logic (fired once for Left, once for Right)
    public static event Action<DefinedCircle, SetupPhase> OnBongoDefined;
    public static event Action OnBothBongosDefined;

    private SetupPhase currentPhase = SetupPhase.DefiningLeftHembra;
    private List<GameObject> createdDrumVisuals = new List<GameObject>();
    private bool inCorrectionMode = false;

    // We override Activate to ensure state is reset correctly every time we start fresh
    public void Activate(bool automatic)
    {
        IsActive = true;
        ResetDefinition();
        currentPhase = SetupPhase.DefiningLeftHembra;
        Debug.Log("Bongo Setup Started: Please define the LEFT drum (Hembra).");

        if (automatic)
        {
            Debug.Log("Bongo Manager activated in CV mode.");
            cvCircleFinder.Activate();
        }
        else
        {
            base.Activate();
        }
    }

    public void Deactivate()
    {
        if (cvCircleFinder != null) cvCircleFinder.Deactivate();
        IsActive = false;
    }

    private void Update()
    {
        if (inCorrectionMode && OVRInput.GetDown(OVRInput.Button.One, OVRInput.Controller.RTouch))
        {
            FinishSetup();
        }
    }

    public void FinishSetup()
    {
        isDefined = true;
        IsActive = false;
        inCorrectionMode = false;
        OnBothBongosDefined.Invoke();
    }

    protected override void UpdateDrawing()
    {
        // 1. Draw the preview lines (connecting the 3 clicks in progress)
        if (currentPhase != SetupPhase.Finished)
        {
            lineRenderer.positionCount = capturedPoints.Count;
            lineRenderer.SetPositions(capturedPoints.ToArray());
            lineRenderer.enabled = capturedPoints.Count > 0;

            // Set color based on current phase
            Color phaseColor = (currentPhase == SetupPhase.DefiningLeftHembra) ? leftDrumColor : rightDrumColor;
            lineRenderer.startColor = phaseColor;
            lineRenderer.endColor = phaseColor;
        }
    }

    public override void ResetDefinition()
    {
        base.ResetDefinition();

        inCorrectionMode = false;
        currentPhase = SetupPhase.DefiningLeftHembra; // Reset logic to start with Left drum

        // 1. Destroy the permanent drum visuals (Left and Right)
        foreach (var drumObj in createdDrumVisuals)
        {
            if (drumObj != null) Destroy(drumObj);
        }
        createdDrumVisuals.Clear();

        // 2. Ensure the preview LineRenderer is clean
        if (lineRenderer != null)
        {
            lineRenderer.positionCount = 0;
            lineRenderer.enabled = false;
        }

        Debug.Log("Bongo Definition Reset.");
    }

    protected override void FinalizeDefinition()
    {
        if (capturedPoints.Count < 3) return;

        try
        {
            // Normalize them to have the same y coordinate first
            float y_Value = (capturedPoints[0].y + capturedPoints[1].y + capturedPoints[2].y) / 3;

            Vector3 circPoint1 = new Vector3(capturedPoints[0].x, y_Value, capturedPoints[0].z);
            Vector3 circPoint2 = new Vector3(capturedPoints[1].x, y_Value, capturedPoints[1].z);
            Vector3 circPoint3 = new Vector3(capturedPoints[2].x, y_Value, capturedPoints[2].z);

            // Calculate the Circle Math
            DefinedCircle newCircle = CalculateCircleFrom3Points(circPoint1, circPoint2, circPoint3);

            // Determine Context (Left vs Right)
            bool isLeft = (currentPhase == SetupPhase.DefiningLeftHembra);
            Color drumColor = isLeft ? leftDrumColor : rightDrumColor;
            string drumName = isLeft ? "Left Drum (Hembra)" : "Right Drum (Macho)";

            // Spawn Permanent Visuals (Separate from the preview LineRenderer)
            SpawnPermanentVisuals(newCircle, drumColor, drumName);

            // Notify Listeners
            Debug.Log($"{drumName} Defined! Radius: {newCircle.Radius:F3}");

            OnBongoDefined?.Invoke(newCircle, currentPhase);


            // Advance State
            if (currentPhase == SetupPhase.DefiningLeftHembra)
            {
                PrepareForNextDrum();
            }
            else
            {
                CompleteSetup();
            }
        }
        catch (Exception e)
        {
            Debug.LogError($"Failed to define bongo (points might be collinear): {e.Message}");
            // Only reset the current points, don't kill the whole manager
            capturedPoints.Clear();
            foreach (var v in spawnedVisuals) Destroy(v); // Only destroys the temp points for this step
            spawnedVisuals.Clear();
        }
    }

    /// <summary>
    /// Clears the input points to allow defining the second drum, 
    /// but keeps the manager active and preserves the first drum's visuals.
    /// </summary>
    private void PrepareForNextDrum()
    {
        currentPhase = SetupPhase.DefiningRightMacho;

        // Clear input data
        capturedPoints.Clear();

        // Clear temporary point markers (the little spheres where user clicked)
        // We assume 'spawnedVisuals' in the base class tracks these temporary markers
        foreach (var obj in spawnedVisuals)
        {
            if (obj != null) Destroy(obj);
        }
        spawnedVisuals.Clear();

        // Reset Preview LineRenderer
        lineRenderer.positionCount = 0;

        Debug.Log("Left Drum Done. Please define the RIGHT drum (Macho).");
    }

    private void CompleteSetup()
    {
        currentPhase = SetupPhase.Finished;
        inCorrectionMode = true;

        // Clear temp markers for the second drum
        foreach (var obj in spawnedVisuals) if (obj != null) Destroy(obj);
        spawnedVisuals.Clear();

        lineRenderer.enabled = false;
        Debug.Log("Bongo Setup Complete.");
    }

    /// <summary>
    /// Creates a dedicated GameObject for the finished circle.
    /// We cannot use the main LineRenderer because we need to draw two separate circles.
    /// </summary>
    private void SpawnPermanentVisuals(DefinedCircle circle, Color color, string name)
    {
        // A. Create the Line Visual
        GameObject circleObj = new GameObject(name + "_Visual");

        createdDrumVisuals.Add(circleObj);

        LineRenderer lr = circleObj.AddComponent<LineRenderer>();

        // Configure LR (Copy settings from main or set defaults)
        lr.useWorldSpace = true;
        lr.loop = true;
        lr.positionCount = circleResolution + 1;
        lr.startWidth = lineRenderer.startWidth;
        lr.endWidth = lineRenderer.endWidth;
        lr.material = lineMaterial != null ? lineMaterial : new Material(Shader.Find("Sprites/Default"));
        lr.startColor = color;
        lr.endColor = color;

        // Generate Points
        Vector3 tangent = Vector3.Cross(circle.Normal, Vector3.up);
        if (tangent.sqrMagnitude < 0.001f) tangent = Vector3.Cross(circle.Normal, Vector3.right);
        tangent.Normalize();
        Vector3 binormal = Vector3.Cross(circle.Normal, tangent).normalized;

        for (int i = 0; i <= circleResolution; i++)
        {
            float angle = i * 2 * Mathf.PI / circleResolution;
            Vector3 point = circle.Center + (circle.Radius * Mathf.Cos(angle) * tangent) + (circle.Radius * Mathf.Sin(angle) * binormal);
            lr.SetPosition(i, point);
        }
    }

    private DefinedCircle CalculateCircleFrom3Points(Vector3 p1, Vector3 p2, Vector3 p3)
    {
        Vector3 v1 = p2 - p1;
        Vector3 v2 = p3 - p1;
        Vector3 normal = Vector3.Cross(v1, v2).normalized;

        // Orient normal to camera
        Vector3 toCamera = Camera.main.transform.position - p1;
        if (Vector3.Dot(normal, toCamera) < 0) normal = -normal;

        Vector3 m1 = (p1 + p2) / 2f;
        Vector3 m2 = (p2 + p3) / 2f;
        Vector3 dir1 = Vector3.Cross(v1, normal).normalized;
        Vector3 dir2 = Vector3.Cross(p3 - p2, normal).normalized;

        float det = Vector3.Dot(Vector3.Cross(dir1, dir2), normal);
        if (Mathf.Abs(det) < 0.001f) throw new Exception("Points are collinear");

        Vector3 p1_to_p2 = m2 - m1;
        float t = Vector3.Dot(Vector3.Cross(p1_to_p2, dir2), normal) / det;

        Vector3 center = m1 + (dir1 * t);
        float radius = Vector3.Distance(center, p1);

        return new DefinedCircle(center, normal, radius);
    }

    private void SetupTransparentMaterial(Material material, Color baseColor)
    {
        material.SetFloat("_Mode", 3);
        material.SetInt("_SrcBlend", (int)UnityEngine.Rendering.BlendMode.SrcAlpha);
        material.SetInt("_DstBlend", (int)UnityEngine.Rendering.BlendMode.OneMinusSrcAlpha);
        material.SetInt("_ZWrite", 0);
        material.DisableKeyword("_ALPHATEST_ON");
        material.EnableKeyword("_ALPHABLEND_ON");
        material.DisableKeyword("_ALPHAPREMULTIPLY_ON");
        material.renderQueue = (int)UnityEngine.Rendering.RenderQueue.Transparent;

        // Set color with low alpha
        Color c = baseColor;
        c.a = 0.3f;
        material.color = c;
    }
}