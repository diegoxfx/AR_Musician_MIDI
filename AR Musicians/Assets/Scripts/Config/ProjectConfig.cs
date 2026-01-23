using UnityEngine;

public static class ProjectConfig
{
    private static ProjectSettings _settings;
    public static ProjectSettings Settings
    {
        get
        {
            if (_settings == null)
            {
                _settings = Resources.Load<ProjectSettings>("ProjectSettings");
                if (_settings == null)
                {
                    Debug.LogError("ProjectSettings asset not found in Resources folder!");
                }
            }
            return _settings;
        }
    }
}
