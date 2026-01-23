using System.Collections.Generic;
using UnityEngine;

[RequireComponent(typeof(LineRenderer))]
public abstract class InstrumentDefiner : MonoBehaviour, IPointInputListener
{
    [Header("Base Settings")]
    [SerializeField] protected GameObject pointPrefab;
    [SerializeField] protected float pointSize = 0.02f;
    [SerializeField] protected Color visualColor = Color.cyan;
    [SerializeField] protected int requiredPoints = 3;

    protected List<Vector3> capturedPoints = new List<Vector3>();
    protected List<GameObject> spawnedVisuals = new List<GameObject>();
    protected LineRenderer lineRenderer;
    protected bool isDefined = false;

    // Interface Implementation
    public bool IsActive { get; protected set; } = false;

    protected virtual void Awake()
    {
        lineRenderer = GetComponent<LineRenderer>();
        lineRenderer.useWorldSpace = true;
        lineRenderer.startWidth = 0.005f;
        lineRenderer.endWidth = 0.005f;
        lineRenderer.material = new Material(Shader.Find("Sprites/Default"));
        lineRenderer.material.color = visualColor;
        lineRenderer.positionCount = 0;
        lineRenderer.enabled = false;
    }

    public virtual void Activate()
    {
        ResetDefinition();
        IsActive = true;
    }

    public virtual void RegisterPoint(Vector3 worldPosition)
    {
        Debug.Log("Registering point: " + worldPosition);
        if (isDefined) return;
        Debug.Log("Start handling the point");

        capturedPoints.Add(worldPosition);

        // Visuals
        GameObject dot = Instantiate(pointPrefab, worldPosition, Quaternion.identity);
        dot.transform.localScale = Vector3.one * pointSize;
        spawnedVisuals.Add(dot);

        UpdateDrawing();

        if (capturedPoints.Count >= requiredPoints)
        {
            FinalizeDefinition();
        }
    }

    // Child classes implement how the lines connect
    protected abstract void UpdateDrawing();

    // Child classes implement the math to finish the shape
    protected abstract void FinalizeDefinition();

    public virtual void ResetDefinition()
    {
        isDefined = false;
        capturedPoints.Clear();
        foreach (var obj in spawnedVisuals) Destroy(obj);
        spawnedVisuals.Clear();
        lineRenderer.positionCount = 0;
    }
}