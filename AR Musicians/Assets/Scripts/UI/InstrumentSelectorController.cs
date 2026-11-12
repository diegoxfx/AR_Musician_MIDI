using UnityEngine;
using UnityEngine.UI;

public class InstrumentSelectorController : MonoBehaviour
{

    public Button pianoButton;
    public Button bongosButton;

    [SerializeField] private RhythmGameManager rhythmGameManager;
    [SerializeField] private PianoStrategy pianoStrategy;
    [SerializeField] private BongoStrategy bongoStrategy;

    #region Private fields
    private const string PIANO_NAME = "piano";
    private const string BONGOS_NAME = "bongos";
    #endregion

    public void selectPiano()
    {
        rhythmGameManager.SetStrategy(pianoStrategy);
    }

    public void selectBongos()
    {
        rhythmGameManager.SetStrategy(bongoStrategy);
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
