using UnityEngine;
using UnityEngine.UI;

public class InstrumentSelectorController : MonoBehaviour
{

    public Button pianoButton;
    public Button bongosButton;

    #region Private fields
    private const string PIANO_NAME = "piano";
    private const string BONGOS_NAME = "bongos";
    #endregion

    public void selectPiano()
    {
        ProjectConfig.Settings.instrument = PIANO_NAME;
    }

    public void selectBongos()
    {
        ProjectConfig.Settings.instrument = BONGOS_NAME;
    }

    public void onConfiguredNewInstrument()
    {
        if (ProjectConfig.Settings.configured_instruments.Contains(PIANO_NAME))
        {
            pianoButton.interactable = true;
        }
        if (ProjectConfig.Settings.configured_instruments.Contains(BONGOS_NAME))
        {
            bongosButton.interactable = true;
        }
    }
}
