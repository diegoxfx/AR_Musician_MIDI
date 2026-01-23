using System;
using UnityEngine;
using UnityEngine.UI;

public class MenuControllerGeneric : MonoBehaviour
{
    #region Serialized fields
    [Header("UI Menus")]
    public GameObject mainMenu;
    public GameObject songMenu;
    public GameObject instrumentSelectorMenu;
    public GameObject instrumentDetectorMenu;
    public GameObject singlePlayerLobbyMenu;
    public GameObject multiplayerMenu;
    public GameObject createRoomMenu;
    public GameObject joinRoomMenu;
    public GameObject multiplayerLobbyMenu;
    public GameObject singleplayerPauseMenu;
    public GameObject multiplayerPauseMenu;
    public bool instrumentDefined = false;


    [Header("Setup Menus")]
    [SerializeField] private GameObject manualPlaneDefinitionMenu;
    [SerializeField] private GameObject cvPlaneDefinitionMenu;
    [SerializeField] private GameObject manualCircleDefinitionMenu;
    [SerializeField] private GameObject cvCircleDefinitionMenu;

    [Header("Buttons")]
    public Button multiplayerButton;
    [SerializeField] public Button multiplayerStart;
    [SerializeField] public Button singleplayerButton;

    [SerializeField] private Button bongoButton, pianoButton;
    [SerializeField] private Button[] songSelectionButtons;
    [SerializeField] private Button resumeMultiplayerButton;

    [Header("Managers & Strategies")]
    [SerializeField] private RhythmGameManager rhythmGameManager; // The generic game manager
    [SerializeField] private RayCastInputProvider inputProvider;  // Handles the raycasting for setup

    [Space(10)]
    [Header("Piano Components")]
    [SerializeField] private PianoManagerInstrumentDefiner pianoManager;
    [SerializeField] private PianoStrategy pianoStrategy;

    [Space(10)]
    [Header("Bongo Components")]
    [SerializeField] private BongosManager BongosManager;
    [SerializeField] private BongoStrategy bongoStrategy;

    #endregion

    public void Start()
    {
        multiplayerButton.interactable = false;
        // Subscribe to BOTH events
        if (pianoManager != null)
            PianoManagerInstrumentDefiner.OnPlaneDefined += HandlePianoDefined;

        if (BongosManager != null)
            BongosManager.OnBothBongosDefined += HandleBongoDefined;
    }
    public void Update()
    {
        bool multiplayer = ProjectConfig.Settings.enableMultiplayer && ProjectConfig.Settings.useMultiplayer;
        if (!multiplayer && rhythmGameManager.playing && OVRInput.GetDown(OVRInput.Button.One, OVRInput.Controller.RTouch))
        {
            rhythmGameManager.Pause();
            ShowSingleplayerPauseMenu();
        }
    }

    private void OnDestroy()
    {
        // Clean up events to prevent memory leaks
        if (pianoManager != null) PianoManagerInstrumentDefiner.OnPlaneDefined -= HandlePianoDefined;
        if (BongosManager != null) BongosManager.OnBothBongosDefined -= HandleBongoDefined;
    }

    #region Event handlers & Navigation

    public void HideAllMenus()
    {
        mainMenu.SetActive(false);
        songMenu.SetActive(false);
        instrumentSelectorMenu.SetActive(false);
        singlePlayerLobbyMenu.SetActive(false);
        multiplayerMenu.SetActive(false);
        createRoomMenu.SetActive(false);
        joinRoomMenu.SetActive(false);
        instrumentDetectorMenu.SetActive(false);
        manualPlaneDefinitionMenu.SetActive(false);
        cvPlaneDefinitionMenu.SetActive(false);
        cvCircleDefinitionMenu.SetActive(false);
        manualCircleDefinitionMenu.SetActive(false);
        multiplayerLobbyMenu.SetActive(false);
        singleplayerPauseMenu.SetActive(false);
        multiplayerPauseMenu.SetActive(false);
    }
    public void ShowMultiplayerPauseMenu(bool masterPauser)
    {
        HideAllMenus();
        multiplayerPauseMenu.SetActive(true);
        resumeMultiplayerButton.interactable = masterPauser;
    }
    public void ShowSingleplayerPauseMenu()
    {
        HideAllMenus();
        singleplayerPauseMenu.SetActive(true);
    }

    public void ShowMainMenu()
    {
        HideAllMenus();
        singleplayerButton.interactable = instrumentDefined;
        multiplayerButton.interactable = instrumentDefined && ProjectConfig.Settings.enableMultiplayer;
        mainMenu.SetActive(true);
    }

    /// <summary>
    /// Call this via UI Button to start Piano Setup
    /// </summary>
    public void StartPianoSetup()
    {
        HideAllMenus();
        manualPlaneDefinitionMenu.SetActive(true); // Keep existing UI

        // 1. Activate the Manager
        pianoManager.Activate(false); // false = manual mode

        // 2. Tell the Raycaster to talk to the PianoManager
        // (Assuming RayCastInputProvider has a SetListener method as discussed previously)
        // Note: PianoManager needs to implement IPointInputListener, or you keep the old logic for Piano specifically.
        // If PianoManager is NOT updated to IPointInputListener yet, we rely on its internal update loop.
        // But for consistency with the new architecture:
        if (inputProvider != null && pianoManager is IPointInputListener listener)
        {
            inputProvider.SetListener(listener);
        }
    }

    /// <summary>
    /// Call this via UI Button to start Bongo Setup
    /// </summary>
    public void StartBongoSetup()
    {
        HideAllMenus();
        // You might want to duplicate the manualPlaneDefinitionMenu and rename it for Bongos, 
        // or just reuse it if the text is generic enough.
        manualCircleDefinitionMenu.SetActive(true);

        // 1. Activate Bongo Manager
        BongosManager.Activate(false);

        // 2. Tell Raycaster to talk to BongosManager
        if (inputProvider != null)
        {
            inputProvider.SetListener(BongosManager);
        }
    }

    private void HandlePianoDefined(DefinedPlane plane)
    {
        // 1. Stop the Setup Manager
        pianoManager.Deactivate();

        // 2. Configure the Game Manager to use Piano Strategy
        if (rhythmGameManager != null)
        {
            rhythmGameManager.SetStrategy(pianoStrategy);
        }
        pianoButton.interactable = true;
        // 3. Update UI
        HandleInstrumentDefined();
    }

    private void HandleBongoDefined()
    {
        // 1. Stop the Setup Manager (Bongos need 2 circles, so the manager handles checking if it's done)
        // If BongosManager.IsActive becomes false automatically after 2nd drum, we are good.
        BongosManager.Deactivate();

        // 2. Configure Game Manager to use Bongo Strategy
        if (rhythmGameManager != null)
        {
            rhythmGameManager.SetStrategy(bongoStrategy);
        }
        bongoButton.interactable = true;
        // 3. Update UI
        HandleInstrumentDefined();
    }

    private void HandleInstrumentDefined()
    {
        instrumentDefined = true;
        ShowMainMenu();
    }

    public void ShowMultiplayerMenu()
    {
        HideAllMenus();
        multiplayerMenu.SetActive(true);
    }

    public void ShowInstrumentDetectorMenu()
    {
        HideAllMenus();
        instrumentDetectorMenu.SetActive(true);
    }

    public void HideInstrumentDetectorMenu()
    {
        HideAllMenus();
        manualPlaneDefinitionMenu.SetActive(true);
    }

    public void ShowManualPlaneDefinitionMenu()
    {
        // Default to Piano if this is called directly
        StartPianoSetup();
    }

    public void ShowCVPlaneDefinitionMenu()
    {
        HideAllMenus();
        cvPlaneDefinitionMenu.SetActive(true);
        // CV Logic usually specific to Piano for now
        pianoManager.Activate(true);
    }

    public void ShowCVCircleDefinitionMenu()
    {
        HideAllMenus();
        cvCircleDefinitionMenu.SetActive(true);
        BongosManager.Activate(true);
    }

    public void ShowCreateRoomMenu()
    {
        HideAllMenus();
        createRoomMenu.SetActive(true);
    }

    public void ShowJoinRoomMenu()
    {
        HideAllMenus();
        joinRoomMenu.SetActive(true);
    }

    public void ShowInstrumentSelectorMenu()
    {
        HideAllMenus();
        instrumentSelectorMenu.SetActive(true);
    }

    public void ShowSongMenu()
    {
        HideAllMenus();
        songMenu.SetActive(true);
        foreach (Button button in songSelectionButtons)
        {
            button.interactable = ProjectConfig.Settings.master;
        }
    }

    public void ShowSinglePlayerLobbyMenu()
    {
        HideAllMenus();
        Debug.Log("Go to singleplayer lobby");
        singlePlayerLobbyMenu.SetActive(true);
    }

    public void postInstrumentSelectedMenu()
    {
        bool multiplayer = ProjectConfig.Settings.enableMultiplayer && ProjectConfig.Settings.useMultiplayer;
        if (multiplayer) ShowMultiplayerMenu();
        else ShowSongMenu();
    }

    public void preSongChoiceMenu()
    {
        bool multiplayer = ProjectConfig.Settings.enableMultiplayer && ProjectConfig.Settings.useMultiplayer;
        if (multiplayer)
        {
            bool master = ProjectConfig.Settings.master;
            if (master) ShowCreateRoomMenu();
            else ShowJoinRoomMenu();
        }
        else
        {
            ShowInstrumentSelectorMenu();
        }
    }

    public void ShowMultiplayerLobbyMenu()
    {
        HideAllMenus();
        multiplayerLobbyMenu.SetActive(true);
    }

    public void postSongChoiceMenu()
    {
        bool multiplayer = ProjectConfig.Settings.enableMultiplayer && ProjectConfig.Settings.useMultiplayer;
        if (multiplayer) ShowMultiplayerLobbyMenu();
        else ShowSinglePlayerLobbyMenu();
    }

    #endregion
}