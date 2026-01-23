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
    // Changed from NoteCubeManager to the new Generic RhythmGameManager
    public RhythmGameManager rhythmGameManager;

    public float countdownTime = 3f;

    public void StartCountdown()
    {
        if (countdown != null) countdown.SetActive(true);
        StartCoroutine(CountdownRoutine());
    }

    public void ResumeCountdown()
    {
        if (countdown != null) countdown.SetActive(true);
        StartCoroutine(ResumeCountdownRoutine());
    }
    private IEnumerator CountdownRoutine()
    {
        float timeLeft = countdownTime;

        while (timeLeft > 0)
        {
            // Update number display
            if (countdownText != null)
                countdownText.text = Mathf.Ceil(timeLeft).ToString();

            // Update circle fill (1 to 0)
            if (countdownCircle != null)
                countdownCircle.fillAmount = timeLeft % 1f;

            timeLeft -= Time.deltaTime;
            yield return null;
        }

        // Final "GO!"
        if (countdownCircle != null) countdownCircle.fillAmount = 0f;
        if (countdownText != null) countdownText.text = "GO!";

        yield return new WaitForSeconds(0.5f);

        // Hide the countdown UI when done
        if (countdown != null) countdown.SetActive(false);

        // Trigger the generic manager. 
        // This will start Piano OR Bongos depending on what Strategy is set.
        if (rhythmGameManager != null)
        {
            rhythmGameManager.Play();
        }
        else
        {
            Debug.LogError("CountdownUIGeneric: RhythmGameManager is not assigned!");
        }
    }

    private IEnumerator ResumeCountdownRoutine()
    {
        float timeLeft = countdownTime;

        while (timeLeft > 0)
        {
            // Update number display
            if (countdownText != null)
                countdownText.text = Mathf.Ceil(timeLeft).ToString();

            // Update circle fill (1 to 0)
            if (countdownCircle != null)
                countdownCircle.fillAmount = timeLeft % 1f;

            timeLeft -= Time.deltaTime;
            yield return null;
        }

        // Final "GO!"
        if (countdownCircle != null) countdownCircle.fillAmount = 0f;
        if (countdownText != null) countdownText.text = "GO!";

        yield return new WaitForSeconds(0.5f);

        // Hide the countdown UI when done
        if (countdown != null) countdown.SetActive(false);

        // Trigger the generic manager. 
        // This will start Piano OR Bongos depending on what Strategy is set.
        if (rhythmGameManager != null)
        {
            rhythmGameManager.Resume();
        }
        else
        {
            Debug.LogError("CountdownUIGeneric: RhythmGameManager is not assigned!");
        }
    }
}