using UnityEngine;
using System.Collections.Generic;

[RequireComponent(typeof(LineRenderer))]
public class BongoArcBehavior : MonoBehaviour
{
    private LineRenderer lineRenderer;
    private BongoStrategy strategy;

    // Animation State
    private float startTime;
    private float approachDuration;
    private float startRadius;
    private float targetRadius;
    private bool isLeftDrum;
    private bool isRunning;

    // Pausing Logic
    private float timeWhenPaused;

    // Visual Settings
    private const int Resolution = 50;

    public void Configure(BongoStrategy strategy, bool isLeft, float drumRadius, float spawnDistance, float duration, Color color)
    {
        this.strategy = strategy;
        this.isLeftDrum = isLeft;
        this.approachDuration = duration;
        this.targetRadius = drumRadius;
        this.startRadius = drumRadius + spawnDistance;

        lineRenderer = GetComponent<LineRenderer>();
        lineRenderer.useWorldSpace = false;
        lineRenderer.widthMultiplier = 0.008f;

        // We must use a shader that supports Alpha transparency. 
        if (lineRenderer.material == null || lineRenderer.material.shader.name != "Sprites/Default")
        {
            lineRenderer.material = new Material(Shader.Find("Sprites/Default"));
        }

        Gradient gradient = new Gradient();

        // The color stays the same the whole way through
        gradient.SetKeys(
            new GradientColorKey[] {
            new GradientColorKey(color, 0.0f),
            new GradientColorKey(color, 1.0f)
            },
            new GradientAlphaKey[] {
            new GradientAlphaKey(0.0f, 0.0f), // Start Transparent (0%)
            new GradientAlphaKey(1.0f, 0.15f), // Become Solid at 15% length
            new GradientAlphaKey(1.0f, 0.85f), // Stay Solid until 85% length
            new GradientAlphaKey(0.0f, 1.0f)  // End Transparent (100%)
            }
        );

        lineRenderer.colorGradient = gradient;

        // Initialize Timing
        startTime = Time.time;
        isRunning = true;
    }

    private void Update()
    {
        if (!isRunning) return;

        // Calculate progress based on current time vs start time
        float timeElapsed = Time.time - startTime;
        float t = timeElapsed / approachDuration;

        if (t >= 1.0f)
        {
            CompleteNote();
        }
        else
        {
            float currentRadius = Mathf.Lerp(startRadius, targetRadius, t);
            DrawArc(currentRadius);
        }
    }

    private void CompleteNote()
    {
        isRunning = false;
        DrawArc(targetRadius);
        strategy.NotifyNoteDone(isLeftDrum);
        unsubscribe();
        Destroy(gameObject, 0.1f);
    }

    private void DrawArc(float radius)
    {
        float startAngle = isLeftDrum ? 90f : -90f;
        float endAngle = isLeftDrum ? 270f : 90f;

        float startRad = startAngle * Mathf.Deg2Rad;
        float endRad = endAngle * Mathf.Deg2Rad;

        Vector3[] positions = new Vector3[Resolution + 1];

        for (int i = 0; i <= Resolution; i++)
        {
            float t = (float)i / Resolution;
            float currentRad = Mathf.Lerp(startRad, endRad, t);
            float x = Mathf.Cos(currentRad) * radius;
            float z = Mathf.Sin(currentRad) * radius;
            positions[i] = new Vector3(x, 0f, z);
        }

        lineRenderer.positionCount = positions.Length;
        lineRenderer.SetPositions(positions);
    }

    public void OnPause()
    {
        if (!isRunning) return;

        isRunning = false;
        // Mark the exact time we stopped
        timeWhenPaused = Time.time;
    }

    public void OnResume()
    {
        ApplyResumeOffset(0f);
    }

    public void OnResume(float adjustment)
    {
        ApplyResumeOffset(adjustment);
    }

    private void unsubscribe()
    {
        RhythmGameManager.OnPause -= OnPause;
        RhythmGameManager.OnQuit -= OnQuit;
        RhythmGameManager.OnResume -= OnResume;
    }

    private void ApplyResumeOffset(float extraDelay)
    {

        startTime += extraDelay;

        isRunning = true;
    }

    public void OnQuit()
    {
        unsubscribe();
        strategy.NotifyNoteDone();
        Destroy(gameObject);
    }
}