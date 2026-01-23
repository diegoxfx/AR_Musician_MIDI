using UnityEngine;
using TMPro;
using Meta.XR;
using Photon.Pun;
using Photon.Realtime;
using System.Collections.Generic;
using System.Linq;
using UnityEngine.UI;
using Meta.XR.MRUtilityKit; // Added for EnvironmentRaycastManager

public class MultiplayerControllerGeneric : MonoBehaviourPunCallbacks
{
    private TouchScreenKeyboard overlayKeyboard;
    public TMP_InputField createRoomText;
    public TMP_InputField joinRoomText;
    private bool createRoomTyping = false;
    private bool joinRoomTyping = false;

    private bool simulator;
    private List<string> cachedRoomNames = new List<string>();
    private bool master = true;
    private bool readyLocal = false;
    private bool multiplayer = false;

    [Header("Generic References")]
    [SerializeField] private MenuControllerGeneric menuController;
    [SerializeField] private SongsUIHandlerGeneric songsUIHandler;
    [SerializeField] private RhythmGameManager rhythmGameManager; // Changed from NoteCubeManager
    [SerializeField] private CountdownUIGeneric countDownUI;      // Changed from CountdownUI
    [SerializeField] private Toggle readyToggle;

    private int localQueuePos = 0;
    private Dictionary<Player, bool> playersReady;
    private bool masterPauser = false;

    void Start()
    {
        // EnvironmentRaycastManager check
        simulator = !EnvironmentRaycastManager.IsSupported;
        PhotonNetwork.ConnectUsingSettings();

        // Ensure SongsUIHandlerGeneric events are linked
        SongsUIHandlerGeneric.OnNewPosition += OnNewPositionLocal;
        SongsUIHandlerGeneric.OnSelectSong += OnSelectSongLocal;
    }

    private void Update()
    {
        if (!simulator && overlayKeyboard != null && overlayKeyboard.active)
        {
            if (createRoomTyping && createRoomText.text != overlayKeyboard.text)
            {
                createRoomText.text = overlayKeyboard.text;
            }
            else if (joinRoomTyping && joinRoomText.text != overlayKeyboard.text)
            {
                joinRoomText.text = overlayKeyboard.text;
            }
        }
        multiplayer = ProjectConfig.Settings.enableMultiplayer && ProjectConfig.Settings.useMultiplayer;
        if (!masterPauser && multiplayer && rhythmGameManager.playing && OVRInput.GetDown(OVRInput.Button.One, OVRInput.Controller.RTouch))
        {
            OnPauseLocal();
        }
    }

    #region Pause
    public void OnQuitLocal()
    {
        if (master)
        {
            photonView.RPC("OnQuitNetwork", RpcTarget.Others);
        }
    }
    [PunRPC]
    private void OnQuitNetwork()
    {
        rhythmGameManager.Quit();
    }
    // Locally pressed pause button
    private void OnPauseLocal()
    {
        masterPauser = true;
        photonView.RPC("OnPauseNetwork", RpcTarget.All);
    }
    [PunRPC]
    // Someone in the lobby pressed the pause button
    private void OnPauseNetwork()
    {
        if (rhythmGameManager.quit)
            return;
        rhythmGameManager.Pause();
        menuController.ShowMultiplayerPauseMenu(masterPauser || master);
        masterPauser = false;
    }
    public void OnResumeLocal()
    {
        photonView.RPC("OnResumeNetwork", RpcTarget.All);
    }
    [PunRPC]
    private void OnResumeNetwork()
    {
        if (rhythmGameManager.quit)
            return;
        menuController.HideAllMenus();
        countDownUI.ResumeCountdown();
    }
    #endregion

    #region Ready Logic

    public void OnToggleLocal()
    {
        readyLocal = readyToggle.isOn;
        if (master)
        {
            Debug.Log("Toggling master ready to: " + readyLocal);
            if (playersReady == null) playersReady = new Dictionary<Player, bool>();

            playersReady[PhotonNetwork.LocalPlayer] = readyLocal;
            EveryoneReadyCheck();
        }
        else
        {
            photonView.RPC("OnReadyToggleNetwork", RpcTarget.MasterClient, PhotonNetwork.LocalPlayer, readyLocal);
        }
    }

    [PunRPC]
    private void OnReadyToggleNetwork(Player player, bool newReady)
    {
        Debug.Log("Setting player " + player.ActorNumber + " to ready-state: " + newReady);
        if (playersReady == null) playersReady = new Dictionary<Player, bool>();

        playersReady[player] = newReady;
        EveryoneReadyCheck();
    }

    private void EveryoneReadyCheck()
    {
        if (playersReady == null) return;

        bool everyoneReady = true;
        foreach (var ready in playersReady.Values)
        {
            if (!ready) { everyoneReady = false; break; }
        }
        Debug.Log("Everyone is ready: " + everyoneReady);

        // Enable the start button if everyone is ready
        if (menuController.multiplayerStart != null)
            menuController.multiplayerStart.interactable = everyoneReady;
    }

    public void OnStartSongLocal()
    {
        if (master)
        {
            photonView.RPC("OnStartSongNetwork", RpcTarget.All);
        }
    }

    [PunRPC]
    private void OnStartSongNetwork()
    {
        menuController.HideAllMenus();
        countDownUI.StartCountdown();
    }

    #endregion

    #region SongSync

    // Locally chose new song (scrolled), so this would have to be the master
    private void OnNewPositionLocal(int newPosition)
    {
        if (master && multiplayer && PhotonNetwork.InRoom)
        {
            localQueuePos = newPosition;
            photonView.RPC("OnNextPositionNetwork", RpcTarget.Others, localQueuePos);
        }
    }

    // Locally selected song (clicked play/select), so this would have to be the master
    private void OnSelectSongLocal()
    {
        if (master && multiplayer && PhotonNetwork.InRoom)
        {
            photonView.RPC("OnSelectSongNetwork", RpcTarget.Others);
        }
    }

    [PunRPC]
    // Master selected the song, we need to do the same
    private void OnSelectSongNetwork()
    {
        // Using the Generic Manager to load the song
        StartCoroutine(rhythmGameManager.updateSongRoutine(songsUIHandler.SelectedSong));
        menuController.postSongChoiceMenu();
    }

    [PunRPC]
    // Receive new songData (scroll update)
    private void OnNextPositionNetwork(int newPosition)
    {
        localQueuePos = newPosition;
        songsUIHandler.SelectedSong = songsUIHandler.getSongData(localQueuePos);
    }

    [PunRPC]
    // Give the current queue position to the requesting player
    private void OnGiveQueuePosNetwork(Player player)
    {
        photonView.RPC("OnNextPositionNetwork", player, localQueuePos);
    }

    #endregion

    #region Connection


    public bool RoomExists(string name)
    {
        return cachedRoomNames.Contains(name);
    }

    public override void OnConnectedToMaster()
    {
        Debug.Log("Connected to Photon Master Server.");
        PhotonNetwork.JoinLobby();
        ProjectConfig.Settings.enableMultiplayer = true;
        if (menuController.multiplayerButton != null)
            menuController.multiplayerButton.interactable = ProjectConfig.Settings.enableMultiplayer && menuController.instrumentDefined;
    }

    public void CreateRoom()
    {
        string roomName = createRoomText.text;

        // Optional: clear whitespace
        if (string.IsNullOrEmpty(roomName)) roomName = "Room1";

        // Check local cache, but don't rely on it 100% as it might be outdated

        // Create the room. 
        // Note: We keep TTL (Time To Live) at 0 so the room dies instantly when the last player leaves.
        RoomOptions options = new RoomOptions { MaxPlayers = 4, EmptyRoomTtl = 0, PlayerTtl = 0 };
        PhotonNetwork.CreateRoom(roomName, options, TypedLobby.Default);
    }

    public override void OnRoomListUpdate(List<RoomInfo> roomList)
    {
        // Update the cache logic to handle removals properly
        foreach (RoomInfo info in roomList)
        {
            // If RemovedFromList is true, the room is gone or full/closed
            if (info.RemovedFromList)
            {
                if (cachedRoomNames.Contains(info.Name))
                {
                    cachedRoomNames.Remove(info.Name);
                }
            }
            else
            {
                // Add if not present
                if (!cachedRoomNames.Contains(info.Name))
                {
                    cachedRoomNames.Add(info.Name);
                }
            }
        }
    }

    public void JoinRoom()
    {
        string roomName = joinRoomText.text;

        if (string.IsNullOrEmpty(roomName)) roomName = "Room1";

        if (!RoomExists(roomName))
        {
            Debug.LogError("Room " + roomName + " does not exist in cache!");
            return;
        }

        PhotonNetwork.JoinRoom(roomName);
    }

    public override void OnJoinedRoom()
    {
        base.OnJoinedRoom();
        Debug.Log("OnJoinedRoom: Successfully connected to room.");

        // Initialize the dictionary immediately to avoid NullReference later
        playersReady = new Dictionary<Player, bool>();

        // Check if we are the Master Client (Creator)
        if (PhotonNetwork.IsMasterClient)
        {
            master = true;
            ProjectConfig.Settings.master = true;

            // Set local player state
            playersReady[PhotonNetwork.LocalPlayer] = false;

            // Show the song selection menu for the Host
            menuController.ShowSongMenu();
        }
        else
        {
            master = false;
            ProjectConfig.Settings.master = false;

            // Ask Master for the current song / queue position
            photonView.RPC("OnGiveQueuePosNetwork", RpcTarget.MasterClient, PhotonNetwork.LocalPlayer);

            // Show the menu (or a "Waiting for Host" screen if you have one)
            menuController.ShowSongMenu();
        }
    }

    //[PunRPC]
    //public void OnKick()
    //{
    //    if (!master)
    //        PhotonNetwork.LeaveRoom(false);
    //    Debug.LogError("You were kicked.");
    //}

    public void LeaveRoom()
    {
        if (!PhotonNetwork.InRoom) return;

        // FIX: If we are the host, we must 'destroy' the room logically before leaving.
        if (PhotonNetwork.IsMasterClient)
        {
            // 1. Make the room invisible and closed.
            // This immediately removes it from the Lobby list for others.
            PhotonNetwork.CurrentRoom.IsVisible = false;
            PhotonNetwork.CurrentRoom.IsOpen = false;

            // 2. Kick everyone else to ensure they don't get stuck in a master-less room.
            photonView.RPC("OnKick", RpcTarget.Others);
        }

        // 3. Leave the room locally
        PhotonNetwork.LeaveRoom(false);
    }

    // Called when the local player leaves the room (voluntarily or after being kicked)
    public override void OnLeftRoom()
    {
        base.OnLeftRoom();

        // Cleanup local state
        playersReady = new Dictionary<Player, bool>();


        // Reset UI
        menuController.preSongChoiceMenu();

        master = true; // Default back to true for singleplayer logic
        ProjectConfig.Settings.master = true;
    }

    [PunRPC]
    public void OnKick()
    {
        // Receive the kick command and leave gracefully
        PhotonNetwork.LeaveRoom(false);
        Debug.Log("The Host ended the session.");

        // Optional: Show a UI message like "Host disbanded the room"
    }

    // FAILSAFE: If the Master crashes (Alt+F4) and didn't call LeaveRoom(),
    // Photon will promote a new player to Master. We must catch this.
    public override void OnMasterClientSwitched(Player newMasterClient)
    {
        // If we are still in the room and the original host is gone,
        // we (the new master or clients) should just leave to prevent a zombie state.
        LeaveRoom();
    }

    //public override void OnJoinedRoom()
    //{
    //    base.OnJoinedRoom();
    //    // New Client joins -> Ask Master for current song
    //    photonView.RPC("OnGiveQueuePosNetwork", RpcTarget.MasterClient, PhotonNetwork.LocalPlayer);
    //    if (master)
    //    {
    //        if (playersReady == null) playersReady = new Dictionary<Player, bool>();
    //        playersReady[PhotonNetwork.LocalPlayer] = false;
    //    }
    //}

    public override void OnPlayerEnteredRoom(Player newPlayer)
    {
        if (master)
        {
            if (playersReady == null) playersReady = new Dictionary<Player, bool>();
            playersReady[newPlayer] = false;
        }
    }

    public override void OnPlayerLeftRoom(Player otherPlayer)
    {
        base.OnPlayerLeftRoom(otherPlayer);
        if (master && playersReady != null)
        {
            playersReady.Remove(otherPlayer);
        }
    }

    //public override void OnLeftRoom()
    //{
    //    base.OnLeftRoom();
    //    menuController.preSongChoiceMenu();
    //    master = true;
    //    ProjectConfig.Settings.master = true;
    //}

    public override void OnDisconnected(DisconnectCause cause)
    {
        Debug.LogWarning("Disconnected from Photon: " + cause);
    }

    #endregion

    #region UI Handlers

    public void OnMultiplayer()
    {
        ProjectConfig.Settings.useMultiplayer = true;
    }

    public void OnSingleplayer()
    {
        ProjectConfig.Settings.useMultiplayer = false;
        master = true;
        ProjectConfig.Settings.master = true;
    }

    public void OnSelectTextCreateRoom()
    {
        Debug.Log("OnSelect Create Room");
        if (!simulator)
            overlayKeyboard = TouchScreenKeyboard.Open("", TouchScreenKeyboardType.Default);
        createRoomTyping = true;
        joinRoomTyping = false;
    }

    public void OnSelectTextJoinRoom()
    {
        Debug.Log("OnSelect Join Room");
        if (!simulator)
            overlayKeyboard = TouchScreenKeyboard.Open("", TouchScreenKeyboardType.Default);
        joinRoomTyping = true;
        createRoomTyping = false;
    }

    public void OnDeselectTextCreateRoom()
    {
        createRoomTyping = false;
        if (simulator) createRoomText.text = "Room1";
        Debug.Log("Create Room Text: " + createRoomText.text);
    }

    public void OnDeselectTextJoinRoom()
    {
        joinRoomTyping = false;
        if (simulator) joinRoomText.text = "Room1"; // If you want identical behavior
        else if (string.IsNullOrEmpty(joinRoomText.text)) joinRoomText.text = "Room1"; // Fallback

        Debug.Log("Join Room Text: " + joinRoomText.text);
    }

    #endregion 
}