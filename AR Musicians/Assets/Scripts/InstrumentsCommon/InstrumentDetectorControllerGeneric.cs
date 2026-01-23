using UnityEngine;

public class InstrumentDetectorControllerGeneric : MonoBehaviour
{
    [Header("Configuration")]
    [Tooltip("Default state of the toggle")]
    [SerializeField] private bool automatic = true;

    [Header("References")]
    [SerializeField] private MenuControllerGeneric menuController;

    /// <summary>
    /// Linked to the UI Toggle for "Automatic / Manual"
    /// </summary>
    public void OnToggle(bool automatic)
    {
        this.automatic = automatic;
        Debug.Log($"Detection mode set to: {(automatic ? "Automatic (CV)" : "Manual (Points)")}");
    }

    /// <summary>
    /// Called when the user clicks the "Piano" button
    /// </summary>
    public void OnPianoPress()
    {
        if (menuController == null)
        {
            Debug.LogError("MenuControllerGeneric is not assigned!");
            return;
        }

        if (automatic)
        {
            // Trigger Computer Vision Flow
            menuController.ShowCVPlaneDefinitionMenu();
        }
        else
        {
            // Trigger Manual Point Definition Flow
            menuController.StartPianoSetup();
        }
    }

    /// <summary>
    /// Called when the user clicks the "Bongos" button
    /// </summary>
    public void OnBongosPress()
    {
        if (menuController == null)
        {
            Debug.LogError("MenuControllerGeneric is not assigned!");
            return;
        }

        if (automatic)
        {
            // FUTURE PROOFING: 
            // Currently, CV for Bongos is not implemented. 
            // We log a warning and fall back to Manual setup so the user isn't stuck.
            menuController.ShowCVCircleDefinitionMenu();
        }
        else
        {
            menuController.StartBongoSetup();
        }
    }

    // Example for future extensibility
    /*
    public void OnXylophonePress()
    {
        if (automatic) menuController.StartXylophoneCV();
        else menuController.StartXylophoneManual();
    }
    */
}