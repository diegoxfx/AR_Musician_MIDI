using UnityEngine;
using UnityEngine.UI;
using TMPro;
using System.Collections;

public class CountdownUIGeneric : MonoBehaviour
{
    [Header("UI References")]
    public GameObject countdown;
    public TextMeshProUGUI countdownText;
    public Image countdownCircle;

    [Header("Game Reference")]
    public RhythmGameManager rhythmGameManager;
    public float countdownTime = 3f;

    private Coroutine running;
    private RhythmGameManager subscribedManager;
    private int activeGeneration = -1;

    public void StartCountdown() { Begin(false); }
    public void ResumeCountdown() { Begin(true); }

    private void Begin(bool resume)
    {
        if (rhythmGameManager == null || activeGeneration >= 0) return;
        int generation = rhythmGameManager.BeginCountdown(resume);
        if (generation < 0)
        {
            rhythmGameManager.RestorePlaybackMenu();
            return;
        }
        activeGeneration = generation;
        subscribedManager = rhythmGameManager;
        subscribedManager.CountdownCancelled += Cancel;
        if (countdown != null) countdown.SetActive(true);
        running = StartCoroutine(CountdownRoutine(generation, resume));
    }

    private IEnumerator CountdownRoutine(int generation, bool resume)
    {
        float timeLeft = countdownTime;
        while (timeLeft > 0)
        {
            if (activeGeneration != generation) yield break;
            if (countdownText != null) countdownText.text = Mathf.Ceil(timeLeft).ToString();
            if (countdownCircle != null) countdownCircle.fillAmount = timeLeft % 1f;
            timeLeft -= Time.deltaTime;
            yield return null;
        }
        if (activeGeneration != generation) yield break;
        if (countdownCircle != null) countdownCircle.fillAmount = 0f;
        if (countdownText != null) countdownText.text = AppText.Choose("GO!", "¡YA!");
        yield return new WaitForSeconds(0.5f);
        if (activeGeneration != generation) yield break;

        var manager = subscribedManager;
        Clear();
        if (!manager.CompleteCountdown(generation, resume)) manager.RestorePlaybackMenu();
    }

    private void Cancel()
    {
        if (running != null) StopCoroutine(running);
        Clear();
    }

    private void Clear()
    {
        activeGeneration = -1;
        running = null;
        if (subscribedManager != null) subscribedManager.CountdownCancelled -= Cancel;
        subscribedManager = null;
        if (countdown != null) countdown.SetActive(false);
    }

    private void OnDisable()
    {
        var manager = subscribedManager;
        if (manager != null)
        {
            manager.CancelCountdown();
        }
        Cancel();
    }
}
