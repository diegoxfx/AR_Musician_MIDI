using NUnit;
using System;
using UnityEngine;

/// <summary>
/// Equivalent of <see cref="CubeFall"/>, but instead of falling and stretching, it floats from a start position (side) to the target position (drum center).
/// </summary>
public class BongoNoteBehavior : MonoBehaviour
{
    private BongoStrategy strategy;
    private Vector3 start, end;
    private float duration;
    private float startTime;
    private bool paused = false;
    private float pauseTime = 0;

    public void Configure(BongoStrategy strat, Vector3 s, Vector3 e, float time)
    {
        strategy = strat;
        start = s;
        end = e;
        duration = time;
        startTime = Time.time;
    }
    public void OnPause()
    {
        paused = true;
    }

    public void OnResume(float newPauseTime)
    {
        pauseTime += newPauseTime;
        paused = false;
    }

    private void unsubscribe()
    {
        RhythmGameManager.OnPause -= OnPause;
        RhythmGameManager.OnQuit -= OnQuit;
        RhythmGameManager.OnResume -= OnResume;
    }
    public void OnQuit()
    {
        unsubscribe();
        strategy.NotifyNoteDone();
        Destroy(gameObject);
    }
    void Update()
    {
        if (paused)
            return;
        float t = (Time.time - startTime - pauseTime) / duration;
        if (t > 1.0f)
        {
            unsubscribe();
            strategy.NotifyNoteDone();
            Destroy(gameObject);
            return;
        }

        transform.position = Vector3.Lerp(start, end, t);
    }
}