using System.Collections.Generic;
using UnityEngine;

[CreateAssetMenu(fileName = "ProjectSettings", menuName = "Config/Project Settings")]
public class ProjectSettings : ScriptableObject
{
    [Header("General Settings")]
    public bool enableMultiplayer = true;

    public string instrument = "piano";

    public bool useMultiplayer = false;

    public List<string> configured_instruments;

    // can this player choose songs etc. Only relevant for multiplayer, should always be true otherwise
    public bool master = true;

    public bool playing = false;
}
